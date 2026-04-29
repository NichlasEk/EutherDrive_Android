using System;

namespace EutherDrive.Core.Arcade.Cps1;

// Pure C# port of MAME's BSD-3-Clause QSound HLE device by superctr and Valley Bell.
internal sealed class Cps1QSound
{
    private const int DspSampleRate = 60_000_000 / 2 / 1248;
    private const int OutputSampleRate = 44_100;
    private const double OutputLowPassCutoffHz = 10_800.0;

    private const int DataPanTable = 0x110;
    private const int DataAdpcmTable = 0x9dc;
    private const int DataFilterTable = 0xd53;
    private const int StateBoot = 0x000;
    private const int StateInit1 = 0x288;
    private const int StateInit2 = 0x61a;
    private const int StateRefresh1 = 0x039;
    private const int StateRefresh2 = 0x04f;
    private const int StateNormal1 = 0x314;
    private const int StateNormal2 = 0x6b2;

    private const int PanTableDry = 0;
    private const int PanTableWet = 98;
    private const int PanTableChannelOffset = 196;
    private const int FilterEntrySize = 95;
    private const int DelayBaseOffset = 0x554;
    private const int DelayBaseOffset2 = 0x53c;

    private readonly ushort[] _dspRom = new ushort[0x1000];
    private byte[] _sampleRom = Array.Empty<byte>();

    private readonly Voice[] _voices = CreateArray(16, static () => new Voice());
    private readonly Adpcm[] _adpcm = CreateArray(3, static () => new Adpcm());
    private readonly ushort[] _voicePan = new ushort[19];
    private readonly short[] _voiceOutput = new short[19];
    private readonly Fir[] _filter = CreateArray(2, static () => new Fir());
    private readonly Fir[] _altFilter = CreateArray(2, static () => new Fir());
    private readonly Delay[] _wet = CreateArray(2, static () => new Delay());
    private readonly Delay[] _dry = CreateArray(2, static () => new Delay());
    private readonly short[] _out = new short[2];
    private readonly Echo _echo = new();

    private ushort _dataLatch;
    private ushort _state;
    private ushort _nextState;
    private ushort _delayUpdate;
    private int _stateCounter;
    private byte _readyFlag;
    private double _resampleAccumulator;
    private int _debugWriteCount;
    private int _debugStatusReadCount;
    private int _debugUpdateCount;
    private byte _debugLastWriteAddress;
    private ushort _debugLastWriteData;
    private readonly short[] _resamplePrevious = new short[2];
    private readonly short[] _resampleNext = new short[2];
    private readonly BiquadLowPass _outputLowPassLeftA = new(OutputSampleRate, OutputLowPassCutoffHz);
    private readonly BiquadLowPass _outputLowPassLeftB = new(OutputSampleRate, OutputLowPassCutoffHz);
    private readonly BiquadLowPass _outputLowPassRightA = new(OutputSampleRate, OutputLowPassCutoffHz);
    private readonly BiquadLowPass _outputLowPassRightB = new(OutputSampleRate, OutputLowPassCutoffHz);
    private bool _resamplePrimed;

    internal int DebugWriteCount => _debugWriteCount;
    internal int DebugStatusReadCount => _debugStatusReadCount;
    internal int DebugUpdateCount => _debugUpdateCount;
    internal int DebugState => _state;
    internal int DebugNextState => _nextState;
    internal int DebugReadyFlag => _readyFlag;
    internal int DebugLastWriteAddress => _debugLastWriteAddress;
    internal int DebugLastWriteData => _debugLastWriteData;
    internal int DebugOutputLeft => _out[0];
    internal int DebugOutputRight => _out[1];

    public void Load(byte[] sampleRom, byte[] dspRom)
    {
        _sampleRom = sampleRom;
        for (int i = 0; i < _dspRom.Length; i++)
            _dspRom[i] = (ushort)(dspRom[i * 2] | (dspRom[i * 2 + 1] << 8));

        Reset();
    }

    public void Reset()
    {
        _dataLatch = 0;
        _out[0] = 0;
        _out[1] = 0;
        _state = StateBoot;
        _nextState = 0;
        _delayUpdate = 0;
        _stateCounter = 0;
        _readyFlag = 0;
        _resampleAccumulator = 0;
        _debugWriteCount = 0;
        _debugStatusReadCount = 0;
        _debugUpdateCount = 0;
        _debugLastWriteAddress = 0;
        _debugLastWriteData = 0;
        _resamplePrevious[0] = 0;
        _resamplePrevious[1] = 0;
        _resampleNext[0] = 0;
        _resampleNext[1] = 0;
        _outputLowPassLeftA.Reset();
        _outputLowPassLeftB.Reset();
        _outputLowPassRightA.Reset();
        _outputLowPassRightB.Reset();
        _resamplePrimed = false;
    }

    public void Write(int offset, byte data)
    {
        switch (offset)
        {
            case 0:
                _dataLatch = (ushort)((_dataLatch & 0x00ff) | (data << 8));
                break;
            case 1:
                _dataLatch = (ushort)((_dataLatch & 0xff00) | data);
                break;
            case 2:
                WriteData(data, _dataLatch);
                _debugWriteCount++;
                _debugLastWriteAddress = data;
                _debugLastWriteData = _dataLatch;
                break;
        }
    }

    public byte ReadStatus()
    {
        _debugStatusReadCount++;
        return _readyFlag;
    }

    public void Render(short[] destination)
    {
        int sampleFrameIndex = 0;
        RenderFrames(destination, ref sampleFrameIndex, destination.Length / 2);
    }

    public void RenderFrames(short[] destination, ref int sampleFrameIndex, int targetSampleFrames)
    {
        EnsureResamplerPrimed();
        double step = DspSampleRate / (double)OutputSampleRate;
        int sampleFrames = destination.Length / 2;
        targetSampleFrames = Math.Clamp(targetSampleFrames, 0, sampleFrames);
        while (sampleFrameIndex < targetSampleFrames)
        {
            int destinationIndex = sampleFrameIndex * 2;
            double left = Interpolate(_resamplePrevious[0], _resampleNext[0], _resampleAccumulator);
            double right = Interpolate(_resamplePrevious[1], _resampleNext[1], _resampleAccumulator);
            left = _outputLowPassLeftB.Apply(_outputLowPassLeftA.Apply(left));
            right = _outputLowPassRightB.Apply(_outputLowPassRightA.Apply(right));
            destination[destinationIndex] = ClampToShort(left);
            destination[destinationIndex + 1] = ClampToShort(right);
            sampleFrameIndex++;

            _resampleAccumulator += step;
            while (_resampleAccumulator >= 1.0)
            {
                _resamplePrevious[0] = _resampleNext[0];
                _resamplePrevious[1] = _resampleNext[1];
                UpdateSample();
                _resampleNext[0] = _out[0];
                _resampleNext[1] = _out[1];
                _resampleAccumulator -= 1.0;
            }
        }
    }

    private void EnsureResamplerPrimed()
    {
        if (_resamplePrimed)
            return;

        _resamplePrevious[0] = _out[0];
        _resamplePrevious[1] = _out[1];
        UpdateSample();
        _resampleNext[0] = _out[0];
        _resampleNext[1] = _out[1];
        _resampleAccumulator = 0;
        _resamplePrimed = true;
    }

    private static double Interpolate(short previous, short next, double fraction)
        => previous + (next - previous) * fraction;

    private static short ClampToShort(double value)
        => (short)Math.Clamp((int)Math.Round(value), short.MinValue, short.MaxValue);

    private void WriteData(byte address, ushort data)
    {
        if (address < 0x80)
        {
            int voice = address >> 3;
            switch (address & 0x07)
            {
                case 0:
                    _voices[(voice + 1) & 0x0f].Bank = data;
                    break;
                case 1:
                    _voices[voice].Addr = unchecked((short)data);
                    break;
                case 2:
                    _voices[voice].Rate = data;
                    break;
                case 3:
                    _voices[voice].Phase = data;
                    break;
                case 4:
                    _voices[voice].LoopLen = unchecked((short)data);
                    break;
                case 5:
                    _voices[voice].EndAddr = unchecked((short)data);
                    break;
                case 6:
                    _voices[voice].Volume = unchecked((short)data);
                    break;
            }
        }
        else if (address >= 0x80 && address <= 0x8f)
        {
            _voicePan[address - 0x80] = data;
        }
        else if (address >= 0x90 && address <= 0x92)
        {
            _voicePan[16 + address - 0x90] = data;
        }
        else if (address == 0x93)
        {
            _echo.Feedback = unchecked((short)data);
        }
        else if (address >= 0xba && address <= 0xc9)
        {
            _voices[address - 0xba].Echo = unchecked((short)data);
        }
        else if (address >= 0xca && address <= 0xd5)
        {
            int adpcm = (address - 0xca) >> 2;
            switch ((address - 0xca) & 0x03)
            {
                case 0:
                    _adpcm[adpcm].StartAddr = data;
                    break;
                case 1:
                    _adpcm[adpcm].EndAddr = data;
                    break;
                case 2:
                    _adpcm[adpcm].Bank = data;
                    break;
                case 3:
                    _adpcm[adpcm].Volume = unchecked((short)data);
                    break;
            }
        }
        else if (address >= 0xd6 && address <= 0xd8)
        {
            _adpcm[address - 0xd6].Flag = data;
        }
        else if (address == 0xd9)
        {
            _echo.EndPos = data;
        }
        else if (address >= 0xda && address <= 0xdd)
        {
            int channel = (address - 0xda) >> 1;
            if ((address & 1) == 0)
                _filter[channel].TablePos = data;
            else
                _altFilter[channel].TablePos = data;
        }
        else if (address >= 0xde && address <= 0xe1)
        {
            int channel = (address - 0xde) >> 1;
            if ((address & 1) == 0)
                _wet[channel].DelaySamples = unchecked((short)data);
            else
                _dry[channel].DelaySamples = unchecked((short)data);
        }
        else if (address == 0xe2)
        {
            _delayUpdate = data;
        }
        else if (address == 0xe3)
        {
            _nextState = data;
        }
        else if (address >= 0xe4 && address <= 0xe7)
        {
            int channel = (address - 0xe4) >> 1;
            if ((address & 1) == 0)
                _wet[channel].Volume = unchecked((short)data);
            else
                _dry[channel].Volume = unchecked((short)data);
        }

        _readyFlag = 0;
    }

    private ushort ReadDspRom(int address)
        => _dspRom[address & 0x0fff];

    private short ReadSample(ushort bank, ushort address)
    {
        int romAddress = ((bank & 0x7fff) << 16) | address;
        byte value = (uint)romAddress < _sampleRom.Length ? _sampleRom[romAddress] : (byte)0xff;
        return unchecked((short)(value << 8));
    }

    private void UpdateSample()
    {
        _debugUpdateCount++;
        switch (_state)
        {
            default:
            case StateInit1:
            case StateInit2:
                StateInit();
                break;
            case StateRefresh1:
                StateRefreshFilter1();
                break;
            case StateRefresh2:
                StateRefreshFilter2();
                break;
            case StateNormal1:
            case StateNormal2:
                StateNormalUpdate();
                break;
        }
    }

    private void StateInit()
    {
        bool mode2 = _state == StateInit2;
        if (_stateCounter >= 2)
        {
            _stateCounter = 0;
            _state = _nextState;
            return;
        }
        if (_stateCounter == 1)
        {
            _stateCounter++;
            return;
        }

        foreach (Voice voice in _voices)
            voice.Reset();
        foreach (Adpcm adpcm in _adpcm)
            adpcm.Reset();
        foreach (Fir filter in _filter)
            filter.Reset();
        foreach (Fir filter in _altFilter)
            filter.Reset();
        foreach (Delay delay in _wet)
            delay.Reset();
        foreach (Delay delay in _dry)
            delay.Reset();
        _echo.Reset();
        Array.Clear(_voiceOutput);

        for (int i = 0; i < _voicePan.Length; i++)
            _voicePan[i] = DataPanTable + 0x10;

        for (int i = 0; i < _voices.Length; i++)
            _voices[i].Bank = 0x8000;
        for (int i = 0; i < _adpcm.Length; i++)
            _adpcm[i].Bank = 0x8000;

        if (!mode2)
        {
            _wet[0].DelaySamples = 0;
            _dry[0].DelaySamples = 46;
            _wet[1].DelaySamples = 0;
            _dry[1].DelaySamples = 48;
            _filter[0].TablePos = DataFilterTable + FilterEntrySize;
            _filter[1].TablePos = DataFilterTable + FilterEntrySize * 2;
            _echo.EndPos = DelayBaseOffset + 6;
            _nextState = StateRefresh1;
        }
        else
        {
            _wet[0].DelaySamples = 1;
            _dry[0].DelaySamples = 0;
            _wet[1].DelaySamples = 0;
            _dry[1].DelaySamples = 0;
            _filter[0].TablePos = 0xf73;
            _filter[1].TablePos = 0xfa4;
            _altFilter[0].TablePos = 0xf73;
            _altFilter[1].TablePos = 0xfa4;
            _echo.EndPos = DelayBaseOffset2 + 6;
            _nextState = StateRefresh2;
        }

        _wet[0].Volume = 0x3fff;
        _dry[0].Volume = 0x3fff;
        _wet[1].Volume = 0x3fff;
        _dry[1].Volume = 0x3fff;

        _delayUpdate = 1;
        _readyFlag = 0;
        _stateCounter = 1;
    }

    private void StateRefreshFilter1()
    {
        for (int ch = 0; ch < 2; ch++)
        {
            _filter[ch].DelayPos = 0;
            _filter[ch].TapCount = 95;
            for (int i = 0; i < 95; i++)
                _filter[ch].Taps[i] = unchecked((short)ReadDspRom(_filter[ch].TablePos + i));
        }

        _state = StateNormal1;
        _nextState = StateNormal1;
    }

    private void StateRefreshFilter2()
    {
        for (int ch = 0; ch < 2; ch++)
        {
            _filter[ch].DelayPos = 0;
            _filter[ch].TapCount = 45;
            for (int i = 0; i < 45; i++)
                _filter[ch].Taps[i] = unchecked((short)ReadDspRom(_filter[ch].TablePos + i));

            _altFilter[ch].DelayPos = 0;
            _altFilter[ch].TapCount = 44;
            for (int i = 0; i < 44; i++)
                _altFilter[ch].Taps[i] = unchecked((short)ReadDspRom(_altFilter[ch].TablePos + i));
        }

        _state = StateNormal2;
        _nextState = StateNormal2;
    }

    private void StateNormalUpdate()
    {
        _readyFlag = 0x80;
        _echo.Length = (short)Math.Clamp(
            _state == StateNormal2 ? _echo.EndPos - DelayBaseOffset2 : _echo.EndPos - DelayBaseOffset,
            0,
            1024);

        int echoInput = 0;
        for (int i = 0; i < _voices.Length; i++)
            _voiceOutput[i] = _voices[i].Update(this, ref echoInput);

        int adpcmVoice = _stateCounter % 3;
        _voiceOutput[16 + adpcmVoice] = _adpcm[adpcmVoice].Update(
            this,
            _voiceOutput[16 + adpcmVoice],
            _stateCounter / 3);

        short echoOutput = _echo.Apply(echoInput);
        for (int ch = 0; ch < 2; ch++)
        {
            int wet = ch == 1 ? echoOutput << 14 : 0;
            int dry = ch == 0 ? echoOutput << 14 : 0;

            for (int i = 0; i < _voiceOutput.Length; i++)
            {
                int panIndex = _voicePan[i] + ch * PanTableChannelOffset;
                dry -= _voiceOutput[i] * unchecked((short)ReadDspRom(panIndex + PanTableDry));
                wet -= _voiceOutput[i] * unchecked((short)ReadDspRom(panIndex + PanTableWet));
            }

            dry = Math.Clamp(dry, -0x1fffffff, 0x1fffffff) << 2;
            wet = Math.Clamp(wet, -0x1fffffff, 0x1fffffff) << 2;

            wet = _filter[ch].Apply(unchecked((short)(wet >> 16)));
            if (_state == StateNormal2)
                dry = _altFilter[ch].Apply(unchecked((short)(dry >> 16)));

            int output = _wet[ch].Apply(wet) + _dry[ch].Apply(dry);
            output = (output + 0x2000) & ~0x3fff;
            _out[ch] = (short)Math.Clamp(output >> 14, -0x7fff, 0x7fff);

            if (_delayUpdate != 0)
            {
                _wet[ch].Update();
                _dry[ch].Update();
            }
        }

        _delayUpdate = 0;
        _stateCounter++;
        if (_stateCounter > 5)
        {
            _stateCounter = 0;
            _state = _nextState;
        }
    }

    private static T[] CreateArray<T>(int length, Func<T> factory)
    {
        T[] result = new T[length];
        for (int i = 0; i < result.Length; i++)
            result[i] = factory();
        return result;
    }

    private sealed class Voice
    {
        public ushort Bank;
        public short Addr;
        public ushort Phase;
        public ushort Rate;
        public short LoopLen;
        public short EndAddr;
        public short Volume;
        public short Echo;

        public void Reset()
        {
            Bank = 0;
            Addr = 0;
            Phase = 0;
            Rate = 0;
            LoopLen = 0;
            EndAddr = 0;
            Volume = 0;
            Echo = 0;
        }

        public short Update(Cps1QSound dsp, ref int echoOut)
        {
            short output = unchecked((short)((Volume * dsp.ReadSample(Bank, unchecked((ushort)Addr))) >> 14));
            echoOut += (output * Echo) << 2;

            int newPhase = Rate + ((Addr << 12) | (Phase >> 4));
            if ((newPhase >> 12) >= EndAddr)
                newPhase -= LoopLen << 12;

            newPhase = Math.Clamp(newPhase, -0x8000000, 0x7ffffff);
            Addr = (short)(newPhase >> 12);
            Phase = (ushort)((newPhase << 4) & 0xffff);

            return output;
        }
    }

    private sealed class Adpcm
    {
        public ushort StartAddr;
        public ushort EndAddr;
        public ushort Bank;
        public short Volume;
        public ushort Flag;
        public short CurVol;
        public short StepSize;
        public ushort CurAddr;

        public void Reset()
        {
            StartAddr = 0;
            EndAddr = 0;
            Bank = 0;
            Volume = 0;
            Flag = 0;
            CurVol = 0;
            StepSize = 0;
            CurAddr = 0;
        }

        public short Update(Cps1QSound dsp, short currentSample, int nibble)
        {
            int step;
            if (nibble == 0)
            {
                if (CurAddr == EndAddr)
                    CurVol = 0;

                if (Flag != 0)
                {
                    currentSample = 0;
                    Flag = 0;
                    StepSize = 10;
                    CurVol = Volume;
                    CurAddr = StartAddr;
                }

                step = (sbyte)(dsp.ReadSample(Bank, CurAddr) >> 8);
            }
            else
            {
                step = (sbyte)(dsp.ReadSample(Bank, CurAddr++) >> 4);
            }

            step >>= 4;
            int delta = ((1 + Math.Abs(step << 1)) * StepSize) >> 1;
            if (step <= 0)
                delta = -delta;
            delta += currentSample;
            delta = Math.Clamp(delta, -32768, 32767);

            StepSize = (short)Math.Clamp((dsp.ReadDspRom(DataAdpcmTable + 8 + step) * StepSize) >> 6, 1, 2000);
            return unchecked((short)((delta * CurVol) >> 16));
        }
    }

    private sealed class Fir
    {
        public int TapCount;
        public int DelayPos;
        public int TablePos;
        public readonly short[] Taps = new short[95];
        private readonly short[] _delayLine = new short[95];

        public void Reset()
        {
            TapCount = 0;
            DelayPos = 0;
            TablePos = 0;
            Array.Clear(Taps);
            Array.Clear(_delayLine);
        }

        public int Apply(short input)
        {
            if (TapCount <= 0)
                return 0;

            int output = 0;
            int tap = 0;
            for (; tap < TapCount - 1; tap++)
            {
                output -= (Taps[tap] * _delayLine[DelayPos++]) << 2;
                if (DelayPos >= TapCount - 1)
                    DelayPos = 0;
            }

            output -= (Taps[tap] * input) << 2;
            _delayLine[DelayPos++] = input;
            if (DelayPos >= TapCount - 1)
                DelayPos = 0;

            return output;
        }
    }

    private sealed class Delay
    {
        public short DelaySamples;
        public short Volume;
        private short _writePos;
        private short _readPos;
        private readonly short[] _delayLine = new short[51];

        public void Reset()
        {
            DelaySamples = 0;
            Volume = 0;
            _writePos = 0;
            _readPos = 0;
            Array.Clear(_delayLine);
        }

        public int Apply(int input)
        {
            _delayLine[_writePos++] = unchecked((short)(input >> 16));
            if (_writePos >= _delayLine.Length)
                _writePos = 0;

            int output = _delayLine[_readPos++] * Volume;
            if (_readPos >= _delayLine.Length)
                _readPos = 0;

            return output;
        }

        public void Update()
        {
            int newReadPos = (_writePos - DelaySamples) % _delayLine.Length;
            _readPos = (short)(newReadPos < 0 ? newReadPos + _delayLine.Length : newReadPos);
        }
    }

    private sealed class Echo
    {
        public ushort EndPos;
        public short Feedback;
        public short Length;
        private short _lastSample;
        private readonly short[] _delayLine = new short[1024];
        private short _delayPos;

        public void Reset()
        {
            EndPos = 0;
            Feedback = 0;
            Length = 0;
            _lastSample = 0;
            _delayPos = 0;
            Array.Clear(_delayLine);
        }

        public short Apply(int input)
        {
            int index = Math.Clamp(_delayPos, (short)0, (short)(_delayLine.Length - 1));
            int oldSample = _delayLine[index];
            int lastSample = _lastSample;
            _lastSample = (short)oldSample;
            oldSample = (oldSample + lastSample) >> 1;

            int newSample = input + ((oldSample * Feedback) << 2);
            _delayLine[index] = unchecked((short)(newSample >> 16));

            _delayPos++;
            if (_delayPos >= Length)
                _delayPos = 0;

            return unchecked((short)oldSample);
        }
    }

    private sealed class BiquadLowPass
    {
        private readonly double _b0;
        private readonly double _b1;
        private readonly double _b2;
        private readonly double _a1;
        private readonly double _a2;
        private double _z1;
        private double _z2;

        public BiquadLowPass(int sampleRate, double cutoffHz)
        {
            double nyquist = sampleRate * 0.5;
            cutoffHz = Math.Clamp(cutoffHz, 1.0, nyquist - 1.0);
            double q = 1.0 / Math.Sqrt(2.0);
            double omega = 2.0 * Math.PI * cutoffHz / sampleRate;
            double sin = Math.Sin(omega);
            double cos = Math.Cos(omega);
            double alpha = sin / (2.0 * q);

            double b0 = (1.0 - cos) * 0.5;
            double b1 = 1.0 - cos;
            double b2 = (1.0 - cos) * 0.5;
            double a0 = 1.0 + alpha;
            double a1 = -2.0 * cos;
            double a2 = 1.0 - alpha;

            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        public double Apply(double sample)
        {
            double output = _b0 * sample + _z1;
            _z1 = _b1 * sample - _a1 * output + _z2;
            _z2 = _b2 * sample - _a2 * output;
            return output;
        }

        public void Reset()
        {
            _z1 = 0.0;
            _z2 = 0.0;
        }
    }

}
