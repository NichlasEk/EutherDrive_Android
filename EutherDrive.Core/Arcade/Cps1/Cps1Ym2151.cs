using System;

namespace EutherDrive.Core.Arcade.Cps1;

// YM2151/OPM core for the classic CPS1 audio path.
// This follows the MAME/YMFM OPM register layout, envelope generator and
// operator routing used by CPS1 (8 channels, fixed OPM operator mapping,
// timers, LFO/noise and 4-op algorithms).
internal sealed class Cps1Ym2151
{
    private const int ChannelCount = 8;
    private const int OperatorCount = ChannelCount * 4;
    private const int InputClockHz = 3_579_545;
    private const int SourceSampleRate = InputClockHz / 64;
    private const int WaveformLength = 0x400;
    private const int EnvelopeQuiet = 0x380;

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

    private static readonly ushort[] SinTable =
    {
        0x859,0x6c3,0x607,0x58b,0x52e,0x4e4,0x4a6,0x471,0x443,0x41a,0x3f5,0x3d3,0x3b5,0x398,0x37e,0x365,
        0x34e,0x339,0x324,0x311,0x2ff,0x2ed,0x2dc,0x2cd,0x2bd,0x2af,0x2a0,0x293,0x286,0x279,0x26d,0x261,
        0x256,0x24b,0x240,0x236,0x22c,0x222,0x218,0x20f,0x206,0x1fd,0x1f5,0x1ec,0x1e4,0x1dc,0x1d4,0x1cd,
        0x1c5,0x1be,0x1b7,0x1b0,0x1a9,0x1a2,0x19b,0x195,0x18f,0x188,0x182,0x17c,0x177,0x171,0x16b,0x166,
        0x160,0x15b,0x155,0x150,0x14b,0x146,0x141,0x13c,0x137,0x133,0x12e,0x129,0x125,0x121,0x11c,0x118,
        0x114,0x10f,0x10b,0x107,0x103,0x0ff,0x0fb,0x0f8,0x0f4,0x0f0,0x0ec,0x0e9,0x0e5,0x0e2,0x0de,0x0db,
        0x0d7,0x0d4,0x0d1,0x0cd,0x0ca,0x0c7,0x0c4,0x0c1,0x0be,0x0bb,0x0b8,0x0b5,0x0b2,0x0af,0x0ac,0x0a9,
        0x0a7,0x0a4,0x0a1,0x09f,0x09c,0x099,0x097,0x094,0x092,0x08f,0x08d,0x08a,0x088,0x086,0x083,0x081,
        0x07f,0x07d,0x07a,0x078,0x076,0x074,0x072,0x070,0x06e,0x06c,0x06a,0x068,0x066,0x064,0x062,0x060,
        0x05e,0x05c,0x05b,0x059,0x057,0x055,0x053,0x052,0x050,0x04e,0x04d,0x04b,0x04a,0x048,0x046,0x045,
        0x043,0x042,0x040,0x03f,0x03e,0x03c,0x03b,0x039,0x038,0x037,0x035,0x034,0x033,0x031,0x030,0x02f,
        0x02e,0x02d,0x02b,0x02a,0x029,0x028,0x027,0x026,0x025,0x024,0x023,0x022,0x021,0x020,0x01f,0x01e,
        0x01d,0x01c,0x01b,0x01a,0x019,0x018,0x017,0x017,0x016,0x015,0x014,0x014,0x013,0x012,0x011,0x011,
        0x010,0x00f,0x00f,0x00e,0x00d,0x00d,0x00c,0x00c,0x00b,0x00a,0x00a,0x009,0x009,0x008,0x008,0x007,
        0x007,0x007,0x006,0x006,0x005,0x005,0x005,0x004,0x004,0x004,0x003,0x003,0x003,0x002,0x002,0x002,
        0x002,0x001,0x001,0x001,0x001,0x001,0x001,0x001,0x000,0x000,0x000,0x000,0x000,0x000,0x000,0x000
    };

    private static readonly uint[] AttenuationIncrementTable =
    {
        0x00000000, 0x00000000, 0x10101010, 0x10101010,
        0x10101010, 0x10101010, 0x11101110, 0x11101110,
        0x10101010, 0x10111010, 0x11101110, 0x11111110,
        0x10101010, 0x10111010, 0x11101110, 0x11111110,
        0x10101010, 0x10111010, 0x11101110, 0x11111110,
        0x10101010, 0x10111010, 0x11101110, 0x11111110,
        0x10101010, 0x10111010, 0x11101110, 0x11111110,
        0x10101010, 0x10111010, 0x11101110, 0x11111110,
        0x10101010, 0x10111010, 0x11101110, 0x11111110,
        0x10101010, 0x10111010, 0x11101110, 0x11111110,
        0x10101010, 0x10111010, 0x11101110, 0x11111110,
        0x10101010, 0x10111010, 0x11101110, 0x11111110,
        0x11111111, 0x21112111, 0x21212121, 0x22212221,
        0x22222222, 0x42224222, 0x42424242, 0x44424442,
        0x44444444, 0x84448444, 0x84848484, 0x88848884,
        0x88888888, 0x88888888, 0x88888888, 0x88888888
    };

    private static readonly ushort[] PowerTable =
    {
        0x1fe8,0x1fd4,0x1fbc,0x1fa8,0x1f90,0x1f7c,0x1f68,0x1f50,
        0x1f3c,0x1f24,0x1f10,0x1efc,0x1ee4,0x1ed0,0x1eb8,0x1ea4,
        0x1e90,0x1e7c,0x1e64,0x1e50,0x1e3c,0x1e28,0x1e10,0x1dfc,
        0x1de8,0x1dd4,0x1dc0,0x1da8,0x1d94,0x1d80,0x1d6c,0x1d58,
        0x1d44,0x1d30,0x1d1c,0x1d08,0x1cf4,0x1ce0,0x1ccc,0x1cb8,
        0x1ca4,0x1c90,0x1c7c,0x1c68,0x1c54,0x1c40,0x1c2c,0x1c18,
        0x1c08,0x1bf4,0x1be0,0x1bcc,0x1bb8,0x1ba4,0x1b94,0x1b80,
        0x1b6c,0x1b58,0x1b48,0x1b34,0x1b20,0x1b10,0x1afc,0x1ae8,
        0x1ad4,0x1ac4,0x1ab0,0x1aa0,0x1a8c,0x1a78,0x1a68,0x1a54,
        0x1a44,0x1a30,0x1a20,0x1a0c,0x19fc,0x19e8,0x19d8,0x19c4,
        0x19b4,0x19a0,0x1990,0x197c,0x196c,0x195c,0x1948,0x1938,
        0x1924,0x1914,0x1904,0x18f0,0x18e0,0x18d0,0x18c0,0x18ac,
        0x189c,0x188c,0x1878,0x1868,0x1858,0x1848,0x1838,0x1824,
        0x1814,0x1804,0x17f4,0x17e4,0x17d4,0x17c0,0x17b0,0x17a0,
        0x1790,0x1780,0x1770,0x1760,0x1750,0x1740,0x1730,0x1720,
        0x1710,0x1700,0x16f0,0x16e0,0x16d0,0x16c0,0x16b0,0x16a0,
        0x1690,0x1680,0x1670,0x1664,0x1654,0x1644,0x1634,0x1624,
        0x1614,0x1604,0x15f8,0x15e8,0x15d8,0x15c8,0x15bc,0x15ac,
        0x159c,0x158c,0x1580,0x1570,0x1560,0x1550,0x1544,0x1534,
        0x1524,0x1518,0x1508,0x14f8,0x14ec,0x14dc,0x14d0,0x14c0,
        0x14b0,0x14a4,0x1494,0x1488,0x1478,0x146c,0x145c,0x1450,
        0x1440,0x1430,0x1424,0x1418,0x1408,0x13fc,0x13ec,0x13e0,
        0x13d0,0x13c4,0x13b4,0x13a8,0x139c,0x138c,0x1380,0x1370,
        0x1364,0x1358,0x1348,0x133c,0x1330,0x1320,0x1314,0x1308,
        0x12f8,0x12ec,0x12e0,0x12d4,0x12c4,0x12b8,0x12ac,0x12a0,
        0x1290,0x1284,0x1278,0x126c,0x1260,0x1250,0x1244,0x1238,
        0x122c,0x1220,0x1214,0x1208,0x11f8,0x11ec,0x11e0,0x11d4,
        0x11c8,0x11bc,0x11b0,0x11a4,0x1198,0x118c,0x1180,0x1174,
        0x1168,0x115c,0x1150,0x1144,0x1138,0x112c,0x1120,0x1114,
        0x1108,0x10fc,0x10f0,0x10e4,0x10d8,0x10cc,0x10c0,0x10b4,
        0x10a8,0x10a0,0x1094,0x1088,0x107c,0x1070,0x1064,0x1058,
        0x1050,0x1044,0x1038,0x102c,0x1020,0x1018,0x100c,0x1000
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
    private uint _envCounter;
    private uint _lfoCounter;
    private uint _noiseLfsr;
    private byte _noiseCounter;
    private byte _noiseState;
    private byte _lfoAm;
    private readonly short[,] _lfoWaveform = new short[4, 256];
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
        _envCounter = 0;
        _lfoCounter = 0;
        _noiseLfsr = 1;
        _noiseCounter = 0;
        _noiseState = 0;
        _lfoAm = 0;
        InitializeLfoWaveforms();
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
        ClockEnvelopeCounter();
        int lfoRawPm = ClockNoiseAndLfo();

        int leftMix = 0;
        int rightMix = 0;
        for (int channel = 0; channel < ChannelCount; channel++)
            _channels[channel].Generate(lfoRawPm, ref leftMix, ref rightMix);

        left = (short)Math.Clamp((int)Math.Round(leftMix * gain), short.MinValue, short.MaxValue);
        right = (short)Math.Clamp((int)Math.Round(rightMix * gain), short.MinValue, short.MaxValue);
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

    private void InitializeLfoWaveforms()
    {
        for (int index = 0; index < 256; index++)
        {
            byte am = (byte)(index ^ 0xff);
            sbyte pm = unchecked((sbyte)index);
            _lfoWaveform[0, index] = (short)(am | (pm << 8));

            am = (index & 0x80) != 0 ? (byte)0 : (byte)0xff;
            pm = unchecked((sbyte)(am ^ 0x80));
            _lfoWaveform[1, index] = (short)(am | (pm << 8));

            am = unchecked((byte)(((index & 0x80) != 0 ? index : index ^ 0xff) << 1));
            pm = unchecked((sbyte)((index & 0x40) != 0 ? am : ~am));
            _lfoWaveform[2, index] = (short)(am | (pm << 8));

            _lfoWaveform[3, index] = 0;
        }
    }

    private void ClockEnvelopeCounter()
    {
        if (((++_envCounter) & 0x03) == 3)
            _envCounter++;
    }

    private int ClockNoiseAndLfo()
    {
        int frequency = ((Reg(0x0f) & 0x1f) ^ 0x1f);
        for (int rep = 0; rep < 2; rep++)
        {
            _noiseLfsr <<= 1;
            _noiseLfsr |= (uint)(((_noiseLfsr >> 17) ^ (_noiseLfsr >> 14) ^ 1) & 1);

            if (_noiseCounter++ >= frequency)
            {
                _noiseCounter = 0;
                _noiseState = (byte)((_noiseLfsr >> 17) & 1);
            }
        }

        int rate = Reg(0x18);
        _lfoCounter += (uint)((0x10 | (rate & 0x0f)) << ((rate >> 4) & 0x0f));
        if ((Reg(0x01) & 0x02) != 0)
            _lfoCounter = 0;

        int lfo = (int)((_lfoCounter >> 22) & 0xff);
        int lfoNoise = (int)((_noiseLfsr >> 17) & 0xff);
        _lfoWaveform[3, (lfo + 1) & 0xff] = (short)(lfoNoise | (lfoNoise << 8));

        short ampm = _lfoWaveform[Reg(0x1b) & 0x03, lfo];
        _lfoAm = (byte)(((ampm & 0xff) * (Reg(0x19) & 0x7f)) >> 7);
        return ((sbyte)(ampm >> 8) * (Reg(0x1a) & 0x7f)) >> 7;
    }

    private byte Reg(int address)
        => _registers[address & 0xff];

    private int LfoAmOffset(int channel)
    {
        int sensitivity = Reg(0x38 + channel) & 0x03;
        return sensitivity == 0 ? 0 : _lfoAm << (sensitivity - 1);
    }

    private bool NoiseEnabled
        => (Reg(0x0f) & 0x80) != 0;

    private int NoiseState
        => _noiseState & 1;

    private int ChannelBlockFrequency(int channel)
        => ((Reg(0x28 + channel) & 0x7f) << 6) | (Reg(0x30 + channel) >> 2);

    private int ComputeOperatorPhaseStep(int operatorOffset, int channel, int blockFrequency, int lfoRawPm)
    {
        int keyCode = (blockFrequency >> 8) & 0x1f;
        int detune = (Reg(0x40 + operatorOffset) >> 4) & 0x07;
        int detuneAdjustment = DetuneAdjustment[keyCode, detune & 0x03];
        if ((detune & 0x04) != 0)
            detuneAdjustment = -detuneAdjustment;

        int detune2 = (Reg(0xc0 + operatorOffset) >> 6) & 0x03;
        int delta = Detune2Delta[detune2];
        int pmSensitivity = (Reg(0x38 + channel) >> 4) & 0x07;
        if (pmSensitivity != 0)
        {
            if (pmSensitivity < 6)
                delta += lfoRawPm >> (6 - pmSensitivity);
            else
                delta += lfoRawPm << (pmSensitivity - 5);
        }

        int phaseStep = OpmKeyCodeToPhaseStep(blockFrequency, delta) + detuneAdjustment;

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

    private static int AbsSinAttenuation(int phase)
    {
        if ((phase & 0x100) != 0)
            phase = ~phase;
        return SinTable[phase & 0xff];
    }

    private static int AttenuationToVolume(int attenuation)
    {
        return PowerTable[attenuation & 0xff] >> (attenuation >> 8);
    }

    private static int AttenuationIncrement(int rate, int index)
        => (int)((AttenuationIncrementTable[Math.Clamp(rate, 0, 63)] >> (4 * (index & 7))) & 0x0f);

    private static int EffectiveRate(int rawRate, int ksr)
        => rawRate == 0 ? 0 : Math.Min(rawRate + ksr, 63);

    private static short Mix(short current, int add)
        => (short)Math.Clamp(current + add, short.MinValue, short.MaxValue);

    private sealed class YmChannel
    {
        private readonly Cps1Ym2151 _chip;
        private readonly int _index;
        private readonly YmOperator[] _ops = new YmOperator[4];
        private readonly int[] _opout = new int[8];
        private short _feedback0;
        private short _feedback1;
        private short _feedbackIn;

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
            _feedback0 = 0;
            _feedback1 = 0;
            _feedbackIn = 0;
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

        public void Generate(int lfoRawPm, ref int leftMix, ref int rightMix)
        {
            _feedback0 = _feedback1;
            _feedback1 = _feedbackIn;

            for (int op = 0; op < 4; op++)
                _ops[op].Clock(lfoRawPm);

            int amOffset = _chip.LfoAmOffset(_index);
            int feedbackMod = _feedback == 0 ? 0 : (_feedback0 + _feedback1) >> (10 - _feedback);
            int op1 = _feedbackIn = (short)_ops[0].ComputeVolume(_ops[0].Phase + feedbackMod, amOffset);

            if (!_left && !_right)
                return;

            int[] opout = _opout;
            opout[0] = 0;
            opout[1] = op1;

            int algorithmOps = AlgorithmOps[_algorithm & 7];
            int opmod = opout[algorithmOps & 1] >> 1;
            opout[2] = _ops[1].ComputeVolume(_ops[1].Phase + opmod, amOffset);
            opout[5] = opout[1] + opout[2];

            opmod = opout[(algorithmOps >> 1) & 7] >> 1;
            opout[3] = _ops[2].ComputeVolume(_ops[2].Phase + opmod, amOffset);
            opout[6] = opout[1] + opout[3];
            opout[7] = opout[2] + opout[3];

            int result;
            if (_chip.NoiseEnabled && _index == 7)
            {
                result = _ops[3].ComputeNoiseVolume(amOffset);
            }
            else
            {
                opmod = opout[(algorithmOps >> 4) & 7] >> 1;
                result = _ops[3].ComputeVolume(_ops[3].Phase + opmod, amOffset);
            }

            if (((algorithmOps >> 7) & 1) != 0)
                result = Math.Clamp(result + opout[1], -32768, 32767);
            if (((algorithmOps >> 8) & 1) != 0)
                result = Math.Clamp(result + opout[2], -32768, 32767);
            if (((algorithmOps >> 9) & 1) != 0)
                result = Math.Clamp(result + opout[3], -32768, 32767);

            if (_left)
                leftMix += result;
            if (_right)
                rightMix += result;
        }

        private static readonly int[] AlgorithmOps =
        {
            Algorithm(1, 2, 3, false, false, false),
            Algorithm(0, 5, 3, false, false, false),
            Algorithm(0, 2, 6, false, false, false),
            Algorithm(1, 0, 7, false, false, false),
            Algorithm(1, 0, 3, false, true, false),
            Algorithm(1, 1, 1, false, true, true),
            Algorithm(1, 0, 0, false, true, true),
            Algorithm(0, 0, 0, true, true, true)
        };

        private static int Algorithm(int op2In, int op3In, int op4In, bool op1Out, bool op2Out, bool op3Out)
            => op2In | (op3In << 1) | (op4In << 4) |
               ((op1Out ? 1 : 0) << 7) | ((op2Out ? 1 : 0) << 8) | ((op3Out ? 1 : 0) << 9);
    }

    private sealed class YmOperator
    {
        private readonly Cps1Ym2151 _chip;
        private readonly int _offset;
        private EnvelopeState _state;
        private bool _keyOn;
        private int _channel;
        private int _blockFrequency;
        private uint _phaseStep;
        private uint _phase;
        private ushort _envAttenuation;
        private int _totalLevel;
        private int _sustainLevel;
        private readonly int[] _rate = new int[4];

        public YmOperator(Cps1Ym2151 chip, int offset)
        {
            _chip = chip;
            _offset = offset;
        }

        public void Reset()
        {
            _state = EnvelopeState.Release;
            _keyOn = false;
            _phase = 0;
            _envAttenuation = 0x3ff;
            Refresh();
        }

        public void Refresh()
        {
            RefreshPhaseStep();

            int ar = _chip.Reg(0x80 + _offset) & 0x1f;
            int ksr = (_chip.Reg(0x80 + _offset) >> 6) & 0x03;
            int d1r = _chip.Reg(0xa0 + _offset) & 0x1f;
            int d2r = _chip.Reg(0xc0 + _offset) & 0x1f;
            int rr = _chip.Reg(0xe0 + _offset) & 0x0f;
            int sl = (_chip.Reg(0xe0 + _offset) >> 4) & 0x0f;
            int keyCode = (_blockFrequency >> 8) & 0x1f;
            int ksrValue = keyCode >> (ksr ^ 3);

            _totalLevel = (_chip.Reg(0x60 + _offset) & 0x7f) << 3;
            _rate[(int)EnvelopeState.Attack] = EffectiveRate(ar * 2, ksrValue);
            _rate[(int)EnvelopeState.Decay] = EffectiveRate(d1r * 2, ksrValue);
            _rate[(int)EnvelopeState.Sustain] = EffectiveRate(d2r * 2, ksrValue);
            _rate[(int)EnvelopeState.Release] = EffectiveRate(rr * 4 + 2, ksrValue);

            int sustain = sl | ((sl + 1) & 0x10);
            _sustainLevel = sustain << 5;
        }

        public void SetBlockFrequency(int blockFrequency)
        {
            _blockFrequency = blockFrequency;
            _channel = _offset & 0x07;
            RefreshPhaseStep();
            Refresh();
        }

        public void SetKeyOn(bool keyOn)
        {
            if (keyOn == _keyOn)
                return;

            _keyOn = keyOn;
            if (keyOn)
            {
                _state = EnvelopeState.Attack;
                _phase = 0;
                if (_rate[(int)EnvelopeState.Attack] >= 62)
                    _envAttenuation = 0;
            }
            else
            {
                _state = EnvelopeState.Release;
            }
        }

        public int Phase
            => (int)(_phase >> 10);

        public void Clock(int lfoRawPm)
        {
            ClockEnvelope();
            int step = _chip.ComputeOperatorPhaseStep(_offset, _channel, _blockFrequency, lfoRawPm);
            _phaseStep = (uint)Math.Max(0, step);
            _phase += _phaseStep;
        }

        private void RefreshPhaseStep()
            => _phaseStep = (uint)Math.Max(0, _chip.ComputeOperatorPhaseStep(_offset, _channel, _blockFrequency, 0));

        private void ClockEnvelope()
        {
            if ((_chip._envCounter & 0x03) != 0)
                return;

            if (_state == EnvelopeState.Attack && _envAttenuation == 0)
                _state = EnvelopeState.Decay;
            if (_state == EnvelopeState.Decay && _envAttenuation >= _sustainLevel)
                _state = EnvelopeState.Sustain;

            int rate = _rate[(int)_state];
            int rateShift = rate >> 2;
            uint envCounter = _chip._envCounter >> 2;
            envCounter <<= rateShift;
            if ((envCounter & 0x7ff) != 0)
                return;

            int relevantBits = (int)((envCounter >> (rateShift <= 11 ? 11 : rateShift)) & 0x07);
            int increment = AttenuationIncrement(rate, relevantBits);

            switch (_state)
            {
                case EnvelopeState.Attack:
                    if (rate < 62)
                        _envAttenuation = (ushort)Math.Clamp(_envAttenuation + (((~_envAttenuation) * increment) >> 4), 0, 0x3ff);
                    break;
                case EnvelopeState.Decay:
                case EnvelopeState.Sustain:
                case EnvelopeState.Release:
                    _envAttenuation = (ushort)Math.Clamp(_envAttenuation + increment, 0, 0x3ff);
                    break;
            }
        }

        public int ComputeVolume(int phase, int amOffset)
        {
            int envAttenuation = EnvelopeAttenuation(amOffset);
            if (envAttenuation > EnvelopeQuiet)
                return 0;

            int wrappedPhase = phase & (WaveformLength - 1);
            int sinAttenuation = AbsSinAttenuation(wrappedPhase);
            int result = AttenuationToVolume(sinAttenuation + (envAttenuation << 2));
            return (wrappedPhase & 0x200) != 0 ? -result : result;
        }

        public int ComputeNoiseVolume(int amOffset)
        {
            int result = (EnvelopeAttenuation(amOffset) ^ 0x3ff) << 1;
            return _chip.NoiseState != 0 ? -result : result;
        }

        private int EnvelopeAttenuation(int amOffset)
        {
            int result = _envAttenuation;
            if ((_chip.Reg(0xa0 + _offset) & 0x80) != 0)
                result += amOffset;
            result += _totalLevel;
            return Math.Min(result, 0x3ff);
        }

        private enum EnvelopeState
        {
            Attack,
            Decay,
            Sustain,
            Release
        }
    }
}
