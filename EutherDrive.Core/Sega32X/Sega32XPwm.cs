using EutherDrive.Core.Savestates;

namespace EutherDrive.Core.Sega32X;

internal enum Sega32XPwmOutputDirection : ushort
{
    Off = 0,
    Same = 1,
    Opposite = 2,
    Prohibited = 3,
}

internal sealed class Sega32XPwm
{
    private const int FifoLength = 3;
    private const int DefaultOutputSampleRate = 44_100;
    private const ulong NominalSh2ClockHz = 23_011_361;
    private const ushort TwelveBitMask = 0x0FFF;
    private const double PwmLinearGain = 0.7943282347242815;
    private readonly ushort[] _leftFifo = new ushort[FifoLength];
    private readonly ushort[] _rightFifo = new ushort[FifoLength];
    [NonSerialized] private short[] _audioSamples = new short[4096];
    [NonSerialized] private int _audioSampleCount;
    [NonSerialized] private int _outputSampleRate = DefaultOutputSampleRate;
    [NonSerialized] private ulong _audioCycleAccumulator;
    private int _leftReadIndex;
    private int _leftWriteIndex;
    private int _leftCount;
    private int _rightReadIndex;
    private int _rightWriteIndex;
    private int _rightCount;
    private ulong _cycleCounter = TwelveBitMask;
    private ushort _timerCounter = 16;
    private ushort _control;
    private ushort _leftOutput;
    private ushort _rightOutput;

    [NonSerialized] private double _leftDcBlockerX;
    [NonSerialized] private double _leftDcBlockerY;
    [NonSerialized] private double _rightDcBlockerX;
    [NonSerialized] private double _rightDcBlockerY;
    private const double DcBlockerR = 0.995;

    public ushort CycleRegister { get; private set; }
    public bool Dreq1 { get; private set; }

    public void SaveState(BinaryWriter writer) => StateBinarySerializer.WriteInto(writer, this);

    public void LoadState(BinaryReader reader)
    {
        StateBinarySerializer.ReadInto(reader, this);
        ResetAudioOutputState();
    }

    public void Reset()
    {
        Array.Clear(_leftFifo, 0, _leftFifo.Length);
        Array.Clear(_rightFifo, 0, _rightFifo.Length);
        _leftReadIndex = 0;
        _leftWriteIndex = 0;
        _leftCount = 0;
        _rightReadIndex = 0;
        _rightWriteIndex = 0;
        _rightCount = 0;
        _cycleCounter = TwelveBitMask;
        _timerCounter = 16;
        _control = 0;
        _leftOutput = 0;
        _rightOutput = 0;
        CycleRegister = 0;
        Dreq1 = false;
        ResetAudioOutputState();
    }

    public ushort ReadRegister(uint address)
    {
        return (address & 0xF) switch
        {
            0x0 => _control,
            0x2 => CycleRegister,
            0x4 => (ushort)((IsLeftFifoFull ? 0x8000 : 0) | (IsLeftFifoEmpty ? 0x4000 : 0)),
            0x6 => (ushort)((IsRightFifoFull ? 0x8000 : 0) | (IsRightFifoEmpty ? 0x4000 : 0)),
            0x8 => (ushort)(((IsLeftFifoFull || IsRightFifoFull) ? 0x8000 : 0)
                | ((IsLeftFifoEmpty && IsRightFifoEmpty) ? 0x4000 : 0)),
            _ => 0,
        };
    }

    public void M68kWriteRegister(uint address, ushort value)
    {
        WriteRegister(address, value, sh2Access: false);
    }

    public void Sh2WriteRegister(uint address, ushort value)
    {
        WriteRegister(address, value, sh2Access: true);
    }

    public void SetAudioOutputSampleRate(int sampleRate)
    {
        if (sampleRate <= 0)
            sampleRate = DefaultOutputSampleRate;

        if (_outputSampleRate == sampleRate)
            return;

        _outputSampleRate = sampleRate;
        ResetAudioOutputState();
    }

    public void ResetAudioOutputState()
    {
        _audioCycleAccumulator = 0;
        _audioSampleCount = 0;
        if (_audioSamples.Length > 0)
            Array.Clear(_audioSamples, 0, _audioSamples.Length);
    }

    public int MixAudioInto(Span<short> destination)
    {
        int sampleCount = Math.Min(_audioSampleCount, destination.Length);
        for (int i = 0; i < sampleCount; i++)
            destination[i] = SaturateToInt16(destination[i] + _audioSamples[i]);

        if (sampleCount < _audioSampleCount)
            Array.Copy(_audioSamples, sampleCount, _audioSamples, 0, _audioSampleCount - sampleCount);

        _audioSampleCount -= sampleCount;
        return sampleCount;
    }

    public void Tick(ulong sh2Cycles, Sega32XSystemRegisters systemRegisters)
    {
        if (sh2Cycles == 0)
            return;

        while (sh2Cycles != 0)
        {
            ulong cyclesUntilAudioSample = CyclesUntilNextAudioSample();
            ulong cyclesUntilPwmEvent = CountersStopped ? ulong.MaxValue : (_cycleCounter == 0 ? 1UL : _cycleCounter);
            ulong consumed = Math.Min(sh2Cycles, Math.Min(cyclesUntilAudioSample, cyclesUntilPwmEvent));
            CollectAudioSamples(consumed);
            if (!CountersStopped)
                _cycleCounter -= consumed;
            sh2Cycles -= consumed;

            if (CountersStopped || _cycleCounter != 0)
                continue;

            _cycleCounter = (ulong)(unchecked((ushort)(CycleRegister - 1)) & TwelveBitMask);

            if (!IsLeftFifoEmpty)
                _leftOutput = PopLeftFifo();
            if (!IsRightFifoEmpty)
                _rightOutput = PopRightFifo();

            if (_timerCounter > 0)
                _timerCounter--;

            if (_timerCounter == 0)
            {
                _timerCounter = EffectiveTimerInterval;
                systemRegisters.NotifyPwmTimer();
                Dreq1 |= Dreq1Enabled;
            }
        }
    }

    private ulong CyclesUntilNextAudioSample()
    {
        ulong rate = (ulong)Math.Max(_outputSampleRate, 1);
        ulong remainingNumerator = NominalSh2ClockHz > _audioCycleAccumulator
            ? NominalSh2ClockHz - _audioCycleAccumulator
            : 0;
        ulong cycles = remainingNumerator / rate;
        if (remainingNumerator % rate != 0)
            cycles++;

        return cycles == 0 ? 1UL : cycles;
    }

    private void CollectAudioSamples(ulong sh2Cycles)
    {
        if (sh2Cycles == 0)
            return;

        _audioCycleAccumulator += sh2Cycles * (ulong)Math.Max(_outputSampleRate, 1);
        double leftRaw = CurrentLeftRawAmplitude;
        double rightRaw = CurrentRightRawAmplitude;
        while (_audioCycleAccumulator >= NominalSh2ClockHz)
        {
            _audioCycleAccumulator -= NominalSh2ClockHz;
            
            double leftY = leftRaw - _leftDcBlockerX + DcBlockerR * _leftDcBlockerY;
            _leftDcBlockerX = leftRaw;
            _leftDcBlockerY = leftY;

            double rightY = rightRaw - _rightDcBlockerX + DcBlockerR * _rightDcBlockerY;
            _rightDcBlockerX = rightRaw;
            _rightDcBlockerY = rightY;

            AppendAudioSample(SaturateToInt16((int)Math.Round(leftY)), SaturateToInt16((int)Math.Round(rightY)));
        }
    }

    private void AppendAudioSample(short leftSample, short rightSample)
    {
        if (_audioSampleCount + 2 > _audioSamples.Length)
            Array.Resize(ref _audioSamples, Math.Max(_audioSamples.Length * 2, _audioSampleCount + 2));

        _audioSamples[_audioSampleCount++] = leftSample;
        _audioSamples[_audioSampleCount++] = rightSample;
    }

    public void AcknowledgeDreq1()
    {
        Dreq1 = false;
    }

    private void WriteRegister(uint address, ushort value, bool sh2Access)
    {
        switch (address & 0xF)
        {
            case 0x0:
                if (sh2Access)
                    _control = (ushort)((value & 0x0F00) | (value & 0x0080) | (value & 0x000F));
                else
                    _control = (ushort)((_control & 0x0F80) | (value & 0x000F));
                Dreq1 &= Dreq1Enabled;
                break;
            case 0x2:
                CycleRegister = (ushort)(value & TwelveBitMask);
                if (CycleRegister > 1 && _cycleCounter == 0)
                    _cycleCounter = (ulong)(CycleRegister - 1);
                break;
            case 0x4:
                PushLeftFifo((ushort)(value & TwelveBitMask));
                break;
            case 0x6:
                PushRightFifo((ushort)(value & TwelveBitMask));
                break;
            case 0x8:
                ushort sample = (ushort)(value & TwelveBitMask);
                PushLeftFifo(sample);
                PushRightFifo(sample);
                break;
        }
    }

    private ushort EffectiveTimerInterval =>
        (ushort)(((_control >> 8) & 0x0F) == 0 ? 16 : ((_control >> 8) & 0x0F));

    private bool Dreq1Enabled => (_control & 0x0080) != 0;

    private Sega32XPwmOutputDirection LeftOutputDirection => (Sega32XPwmOutputDirection)(_control & 0x0003);
    private Sega32XPwmOutputDirection RightOutputDirection => (Sega32XPwmOutputDirection)((_control >> 2) & 0x0003);

    private bool CountersStopped =>
        CycleRegister == 1
        || IsOutputOff(LeftOutputDirection) && IsOutputOff(RightOutputDirection);

    private static bool IsOutputOff(Sega32XPwmOutputDirection direction) =>
        direction is Sega32XPwmOutputDirection.Off or Sega32XPwmOutputDirection.Prohibited;

    private bool IsLeftFifoEmpty => _leftCount == 0;
    private bool IsRightFifoEmpty => _rightCount == 0;
    private bool IsLeftFifoFull => _leftCount == FifoLength;
    private bool IsRightFifoFull => _rightCount == FifoLength;

    private void PushLeftFifo(ushort sample)
    {
        if (IsLeftFifoFull)
            _leftReadIndex = (_leftReadIndex + 1) % FifoLength;
        else
            _leftCount++;

        _leftFifo[_leftWriteIndex] = sample;
        _leftWriteIndex = (_leftWriteIndex + 1) % FifoLength;
    }

    private void PushRightFifo(ushort sample)
    {
        if (IsRightFifoFull)
            _rightReadIndex = (_rightReadIndex + 1) % FifoLength;
        else
            _rightCount++;

        _rightFifo[_rightWriteIndex] = sample;
        _rightWriteIndex = (_rightWriteIndex + 1) % FifoLength;
    }

    private ushort PopLeftFifo()
    {
        ushort sample = _leftFifo[_leftReadIndex];
        _leftReadIndex = (_leftReadIndex + 1) % FifoLength;
        _leftCount--;
        return sample;
    }

    private ushort PopRightFifo()
    {
        ushort sample = _rightFifo[_rightReadIndex];
        _rightReadIndex = (_rightReadIndex + 1) % FifoLength;
        _rightCount--;
        return sample;
    }

    private double CurrentLeftRawAmplitude => OutputToRawAmplitude(LeftOutputDirection, _leftOutput, _rightOutput);

    private double CurrentRightRawAmplitude => OutputToRawAmplitude(RightOutputDirection, _rightOutput, _leftOutput);

    private double OutputToRawAmplitude(Sega32XPwmOutputDirection direction, ushort sameSideOutput, ushort oppositeSideOutput)
    {
        if (CountersStopped)
            return 0.0;

        ushort pulseWidth = direction switch
        {
            Sega32XPwmOutputDirection.Same => sameSideOutput,
            Sega32XPwmOutputDirection.Opposite => oppositeSideOutput,
            _ => 0,
        };

        return PulseWidthToRawAmplitude(pulseWidth);
    }

    private double PulseWidthToRawAmplitude(ushort pulseWidth)
    {
        if (CycleRegister <= 1)
            return 0.0;

        ushort maxWidth = (ushort)(unchecked((ushort)(CycleRegister - 1)) & TwelveBitMask);
        if (maxWidth == 0)
            return 0.0;

        ushort clampedWidth = Math.Min(pulseWidth, maxWidth);
        double normalized = (double)clampedWidth / maxWidth;
        return normalized * PwmLinearGain * short.MaxValue;
    }

    private static short SaturateToInt16(int value)
    {
        if (value < short.MinValue)
            return short.MinValue;
        if (value > short.MaxValue)
            return short.MaxValue;
        return (short)value;
    }
}
