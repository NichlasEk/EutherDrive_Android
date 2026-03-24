using System;
using System.Runtime.CompilerServices;

namespace KSNES.AudioProcessing;

public sealed class DSP : IDSP
{
    private static readonly bool PerfStatsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_PERF"), "1", StringComparison.Ordinal);
    public float[] SamplesL { get; private set; } = Array.Empty<float>();
    public float[] SamplesR { get; private set; } = Array.Empty<float>();
    public int SampleOffset { get; set; }

    [NonSerialized]
    private IAPU? _apu;
    [NonSerialized]
    internal ulong PerfCycles;
    [NonSerialized]
    internal ulong PerfProducedSamples;
    [NonSerialized]
    internal ulong PerfEchoWrites;

    private const int BrrBlockLen = 9;
    private const int BrrBufferLen = 12;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp16(int value)
    {
        if (value > short.MaxValue)
            return short.MaxValue;
        if (value < short.MinValue)
            return short.MinValue;
        return value;
    }

    private static readonly int[] Gaussian = [
        0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000,
        0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x002, 0x002, 0x002, 0x002, 0x002,
        0x002, 0x002, 0x003, 0x003, 0x003, 0x003, 0x003, 0x004, 0x004, 0x004, 0x004, 0x004, 0x005, 0x005, 0x005, 0x005,
        0x006, 0x006, 0x006, 0x006, 0x007, 0x007, 0x007, 0x008, 0x008, 0x008, 0x009, 0x009, 0x009, 0x00A, 0x00A, 0x00A,
        0x00B, 0x00B, 0x00B, 0x00C, 0x00C, 0x00D, 0x00D, 0x00E, 0x00E, 0x00F, 0x00F, 0x00F, 0x010, 0x010, 0x011, 0x011,
        0x012, 0x013, 0x013, 0x014, 0x014, 0x015, 0x015, 0x016, 0x017, 0x017, 0x018, 0x018, 0x019, 0x01A, 0x01B, 0x01B,
        0x01C, 0x01D, 0x01D, 0x01E, 0x01F, 0x020, 0x020, 0x021, 0x022, 0x023, 0x024, 0x024, 0x025, 0x026, 0x027, 0x028,
        0x029, 0x02A, 0x02B, 0x02C, 0x02D, 0x02E, 0x02F, 0x030, 0x031, 0x032, 0x033, 0x034, 0x035, 0x036, 0x037, 0x038,
        0x03A, 0x03B, 0x03C, 0x03D, 0x03E, 0x040, 0x041, 0x042, 0x043, 0x045, 0x046, 0x047, 0x049, 0x04A, 0x04C, 0x04D,
        0x04E, 0x050, 0x051, 0x053, 0x054, 0x056, 0x057, 0x059, 0x05A, 0x05C, 0x05E, 0x05F, 0x061, 0x063, 0x064, 0x066,
        0x068, 0x06A, 0x06B, 0x06D, 0x06F, 0x071, 0x073, 0x075, 0x076, 0x078, 0x07A, 0x07C, 0x07E, 0x080, 0x082, 0x084,
        0x086, 0x089, 0x08B, 0x08D, 0x08F, 0x091, 0x093, 0x096, 0x098, 0x09A, 0x09C, 0x09F, 0x0A1, 0x0A3, 0x0A6, 0x0A8,
        0x0AB, 0x0AD, 0x0AF, 0x0B2, 0x0B4, 0x0B7, 0x0BA, 0x0BC, 0x0BF, 0x0C1, 0x0C4, 0x0C7, 0x0C9, 0x0CC, 0x0CF, 0x0D2,
        0x0D4, 0x0D7, 0x0DA, 0x0DD, 0x0E0, 0x0E3, 0x0E6, 0x0E9, 0x0EC, 0x0EF, 0x0F2, 0x0F5, 0x0F8, 0x0FB, 0x0FE, 0x101,
        0x104, 0x107, 0x10B, 0x10E, 0x111, 0x114, 0x118, 0x11B, 0x11E, 0x122, 0x125, 0x129, 0x12C, 0x130, 0x133, 0x137,
        0x13A, 0x13E, 0x141, 0x145, 0x148, 0x14C, 0x150, 0x153, 0x157, 0x15B, 0x15F, 0x162, 0x166, 0x16A, 0x16E, 0x172,
        0x176, 0x17A, 0x17D, 0x181, 0x185, 0x189, 0x18D, 0x191, 0x195, 0x19A, 0x19E, 0x1A2, 0x1A6, 0x1AA, 0x1AE, 0x1B2,
        0x1B7, 0x1BB, 0x1BF, 0x1C3, 0x1C8, 0x1CC, 0x1D0, 0x1D5, 0x1D9, 0x1DD, 0x1E2, 0x1E6, 0x1EB, 0x1EF, 0x1F3, 0x1F8,
        0x1FC, 0x201, 0x205, 0x20A, 0x20F, 0x213, 0x218, 0x21C, 0x221, 0x226, 0x22A, 0x22F, 0x233, 0x238, 0x23D, 0x241,
        0x246, 0x24B, 0x250, 0x254, 0x259, 0x25E, 0x263, 0x267, 0x26C, 0x271, 0x276, 0x27B, 0x280, 0x284, 0x289, 0x28E,
        0x293, 0x298, 0x29D, 0x2A2, 0x2A6, 0x2AB, 0x2B0, 0x2B5, 0x2BA, 0x2BF, 0x2C4, 0x2C9, 0x2CE, 0x2D3, 0x2D8, 0x2DC,
        0x2E1, 0x2E6, 0x2EB, 0x2F0, 0x2F5, 0x2FA, 0x2FF, 0x304, 0x309, 0x30E, 0x313, 0x318, 0x31D, 0x322, 0x326, 0x32B,
        0x330, 0x335, 0x33A, 0x33F, 0x344, 0x349, 0x34E, 0x353, 0x357, 0x35C, 0x361, 0x366, 0x36B, 0x370, 0x374, 0x379,
        0x37E, 0x383, 0x388, 0x38C, 0x391, 0x396, 0x39B, 0x39F, 0x3A4, 0x3A9, 0x3AD, 0x3B2, 0x3B7, 0x3BB, 0x3C0, 0x3C5,
        0x3C9, 0x3CE, 0x3D2, 0x3D7, 0x3DC, 0x3E0, 0x3E5, 0x3E9, 0x3ED, 0x3F2, 0x3F6, 0x3FB, 0x3FF, 0x403, 0x408, 0x40C,
        0x410, 0x415, 0x419, 0x41D, 0x421, 0x425, 0x42A, 0x42E, 0x432, 0x436, 0x43A, 0x43E, 0x442, 0x446, 0x44A, 0x44E,
        0x452, 0x455, 0x459, 0x45D, 0x461, 0x465, 0x468, 0x46C, 0x470, 0x473, 0x477, 0x47A, 0x47E, 0x481, 0x485, 0x488,
        0x48C, 0x48F, 0x492, 0x496, 0x499, 0x49C, 0x49F, 0x4A2, 0x4A6, 0x4A9, 0x4AC, 0x4AF, 0x4B2, 0x4B5, 0x4B7, 0x4BA,
        0x4BD, 0x4C0, 0x4C3, 0x4C5, 0x4C8, 0x4CB, 0x4CD, 0x4D0, 0x4D2, 0x4D5, 0x4D7, 0x4D9, 0x4DC, 0x4DE, 0x4E0, 0x4E3,
        0x4E5, 0x4E7, 0x4E9, 0x4EB, 0x4ED, 0x4EF, 0x4F1, 0x4F3, 0x4F5, 0x4F6, 0x4F8, 0x4FA, 0x4FB, 0x4FD, 0x4FF, 0x500,
        0x502, 0x503, 0x504, 0x506, 0x507, 0x508, 0x50A, 0x50B, 0x50C, 0x50D, 0x50E, 0x50F, 0x510, 0x511, 0x511, 0x512,
        0x513, 0x514, 0x514, 0x515, 0x516, 0x516, 0x517, 0x517, 0x517, 0x518, 0x518, 0x518, 0x518, 0x518, 0x519, 0x519,
    ];

    private static readonly ushort[] EnvelopeRate = [
        ushort.MaxValue, 2048, 1536, 1280,
        1024, 768, 640, 512,
        384, 320, 256, 192,
        160, 128, 96, 80,
        64, 48, 40, 32,
        24, 20, 16, 12,
        10, 8, 6, 5,
        4, 3, 2, 1,
    ];

    private static readonly ushort[] EnvelopeOffset = [
        ushort.MaxValue, 0, 1040,
        536, 0, 1040,
        536, 0, 1040,
        536, 0, 1040,
        536, 0, 1040,
        536, 0, 1040,
        536, 0, 1040,
        536, 0, 1040,
        536, 0, 1040,
        536, 0, 1040,
        0,
        0,
    ];

    private enum EnvelopeMode { Adsr, Gain }
    private enum GainMode { Direct, Custom }
    private enum EnvelopePhase { Attack, Decay, Sustain, Release }

    private sealed class BrrRingBuffer
    {
        private readonly short[] _buffer = new short[BrrBufferLen];
        private int _fillIdx;
        private int _sampleIdx;

        public void Reset()
        {
            _fillIdx = 0;
            _sampleIdx = 0;
            Array.Clear(_buffer, 0, _buffer.Length);
        }

        public void Write(short sample)
        {
            _buffer[_fillIdx] = sample;
            _fillIdx = (_fillIdx + 1) % BrrBufferLen;
        }

        public void ShiftSampleIdx()
        {
            _sampleIdx = (_sampleIdx + 4) % BrrBufferLen;
        }

        public (short older, short old) LastTwoWrittenSamples()
        {
            if (_fillIdx == 0)
                return (_buffer[BrrBufferLen - 2], _buffer[BrrBufferLen - 1]);
            if (_fillIdx == 1)
                return (_buffer[BrrBufferLen - 1], _buffer[0]);
            return (_buffer[_fillIdx - 2], _buffer[_fillIdx - 1]);
        }

        public short this[int index]
        {
            get
            {
                int idx = (_sampleIdx + index) % BrrBufferLen;
                return _buffer[idx];
            }
        }
    }

    private sealed class Voice
    {
        public byte InstrumentNumber;
        public ushort SampleRate;
        public bool PitchModulationEnabled;
        public EnvelopeMode EnvelopeMode;
        public byte AttackRate;
        public byte DecayRate;
        public byte SustainRate;
        public byte SustainLevel;
        public GainMode GainMode;
        public byte GainValue;
        public sbyte VolumeL;
        public sbyte VolumeR;
        public bool KeyedOn;
        public bool KeyedOff;
        public bool OutputNoise;
        public byte LastPitchHWrite;

        public ushort BrrBlockAddress;
        public readonly BrrRingBuffer BrrBuffer = new();
        public ushort BrrDecoderIdx;
        public ushort PitchCounter;
        public ushort EnvelopeLevel;
        public ushort ClippedEnvelopeValue;
        public EnvelopePhase EnvelopePhase;
        public short CurrentSample;
        public bool RestartPending;
        public byte RestartDelayRemaining;
        public bool EndFlagSeen;

        public void Reset()
        {
            InstrumentNumber = 0;
            SampleRate = 0;
            PitchModulationEnabled = false;
            EnvelopeMode = EnvelopeMode.Gain;
            AttackRate = 0;
            DecayRate = 0;
            SustainRate = 0;
            SustainLevel = 0;
            GainMode = GainMode.Direct;
            GainValue = 0;
            VolumeL = 0;
            VolumeR = 0;
            KeyedOn = false;
            KeyedOff = false;
            OutputNoise = false;
            LastPitchHWrite = 0;
            BrrBlockAddress = 0;
            BrrBuffer.Reset();
            BrrDecoderIdx = 0;
            PitchCounter = 0;
            EnvelopeLevel = 0;
            ClippedEnvelopeValue = 0;
            EnvelopePhase = EnvelopePhase.Release;
            CurrentSample = 0;
            RestartPending = false;
            RestartDelayRemaining = 0;
            EndFlagSeen = false;
        }

        public void WritePitchLow(byte value) => SampleRate = (ushort)((SampleRate & 0x3F00) | value);

        public void WritePitchHigh(byte value)
        {
            SampleRate = (ushort)((SampleRate & 0x00FF) | ((value & 0x3F) << 8));
            LastPitchHWrite = value;
        }

        public void WriteAdsrLow(byte value)
        {
            AttackRate = (byte)(value & 0x0F);
            DecayRate = (byte)((value >> 4) & 0x07);
            EnvelopeMode = ((value & 0x80) != 0) ? EnvelopeMode.Adsr : EnvelopeMode.Gain;
        }

        public byte ReadAdsrLow() => (byte)(AttackRate | (DecayRate << 4) | (EnvelopeMode == EnvelopeMode.Adsr ? 0x80 : 0x00));

        public void WriteAdsrHigh(byte value)
        {
            SustainRate = (byte)(value & 0x1F);
            SustainLevel = (byte)(value >> 5);
        }

        public byte ReadAdsrHigh() => (byte)(SustainRate | (SustainLevel << 5));

        public void WriteGain(byte value)
        {
            GainMode = (value & 0x80) != 0 ? GainMode.Custom : GainMode.Direct;
            GainValue = (byte)(value & 0x7F);
        }

        public byte ReadGain() => (byte)((GainMode == GainMode.Custom ? 0x80 : 0x00) | GainValue);

        public byte ReadEnvelope() => (byte)((EnvelopeLevel >> 4) & 0xFF);

        public byte ReadOutput() => (byte)((CurrentSample >> 7) & 0xFF);

        public void WriteKeyOn(bool keyOn)
        {
            KeyedOn = keyOn;
            if (keyOn)
            {
                EnvelopePhase = EnvelopePhase.Attack;
                EnvelopeLevel = 0;
                RestartPending = true;
                KeyedOff = false;
            }
        }

        public void WriteKeyOff(bool keyOff)
        {
            KeyedOff = keyOff;
            if (keyOff)
                EnvelopePhase = EnvelopePhase.Release;
        }

        public void SoftReset()
        {
            WriteKeyOff(true);
            EnvelopeLevel = 0;
        }

        public void Clock(DspRegisters registers, byte[] audioRam, short prevVoiceSample, short noiseOutput)
        {
            if (RestartPending)
            {
                RestartPending = false;
                Restart(registers, audioRam);
            }

            if (RestartDelayRemaining != 0)
            {
                CurrentSample = 0;
                if (RestartDelayRemaining <= 3 && (KeyedOff || registers.SoftReset))
                    EnvelopePhase = EnvelopePhase.Release;

                RestartDelayRemaining--;
                if (RestartDelayRemaining == 0)
                {
                    BrrBuffer.Reset();
                    BrrDecoderIdx = 0;
                    for (int i = 0; i < 2; i++)
                        DecodeBrrGroup(registers.SampleTableAddress, audioRam);
                }
                return;
            }

            short interpolatedSample;
            if (OutputNoise)
            {
                interpolatedSample = noiseOutput;
            }
            else
            {
                int sampleIdx = PitchCounter >> 12;
                int offset = (PitchCounter >> 4) & 0xFF;
                int interp = GaussianInterpolate(BrrBuffer[sampleIdx], BrrBuffer[sampleIdx + 1], BrrBuffer[sampleIdx + 2], BrrBuffer[sampleIdx + 3], offset);
                interpolatedSample = (short)interp;
            }

            ClockEnvelope(registers.GlobalCounter);

            int sample = ((interpolatedSample * EnvelopeLevel) >> 11);
            CurrentSample = (short)sample;

            PitchCounter += SampleRate;
            if (PitchModulationEnabled && !OutputNoise)
            {
                int modulation = ((prevVoiceSample >> 5) * SampleRate) >> 10;
                int mod = PitchCounter + modulation;
                if (mod < 0) mod = 0;
                if (mod > 0x7FFF) mod = 0x7FFF;
                PitchCounter = (ushort)mod;
            }

            if (PitchCounter >= 0x4000)
            {
                PitchCounter -= 0x4000;
                DecodeBrrGroup(registers.SampleTableAddress, audioRam);
                BrrBuffer.ShiftSampleIdx();
            }
        }

        private void Restart(DspRegisters registers, byte[] audioRam)
        {
            int tableAddr = (registers.SampleTableAddress + (InstrumentNumber << 2)) & 0xFFFF;
            ushort startAddr = (ushort)(audioRam[tableAddr] | (audioRam[(tableAddr + 1) & 0xFFFF] << 8));
            BrrBlockAddress = startAddr;
            PitchCounter = 0;
            RestartDelayRemaining = 5;
            EndFlagSeen = false;
        }

        private void DecodeBrrGroup(ushort sampleTableAddress, byte[] audioRam)
        {
            if (BrrDecoderIdx == 16)
            {
                byte prevHeader = audioRam[BrrBlockAddress];
                bool endFlag = (prevHeader & 0x01) != 0;
                if (endFlag)
                {
                    EndFlagSeen = true;
                    int tableAddr = (sampleTableAddress + (InstrumentNumber << 2)) & 0xFFFF;
                    ushort loopAddr = (ushort)(audioRam[(tableAddr + 2) & 0xFFFF] | (audioRam[(tableAddr + 3) & 0xFFFF] << 8));
                    BrrBlockAddress = loopAddr;
                }
                else
                {
                    BrrBlockAddress = (ushort)(BrrBlockAddress + BrrBlockLen);
                }
                BrrDecoderIdx = 0;
            }

            byte header = audioRam[BrrBlockAddress];
            byte shift = (byte)(header >> 4);
            byte filter = (byte)((header >> 2) & 0x03);
            bool loopFlag = (header & 0x02) != 0;
            bool endFlag2 = (header & 0x01) != 0;
            if (endFlag2 && !loopFlag)
            {
                EnvelopePhase = EnvelopePhase.Release;
                EnvelopeLevel = 0;
            }

            int decoderIdx = BrrDecoderIdx;
            int sampleAddr0 = (BrrBlockAddress + 1 + (decoderIdx >> 1)) & 0xFFFF;
            byte pair0 = audioRam[sampleAddr0];
            sbyte nibble0 = (sbyte)pair0;
            nibble0 >>= 4;
            sbyte nibble1 = (sbyte)(pair0 << 4);
            nibble1 >>= 4;

            int sampleAddr1 = (BrrBlockAddress + 2 + (decoderIdx >> 1)) & 0xFFFF;
            byte pair1 = audioRam[sampleAddr1];
            sbyte nibble2 = (sbyte)pair1;
            nibble2 >>= 4;
            sbyte nibble3 = (sbyte)(pair1 << 4);
            nibble3 >>= 4;
            BrrDecoderIdx += 4;

            var (older, old) = BrrBuffer.LastTwoWrittenSamples();
            WriteDecodedNibble(nibble0, shift, filter, ref older, ref old);
            WriteDecodedNibble(nibble1, shift, filter, ref older, ref old);
            WriteDecodedNibble(nibble2, shift, filter, ref older, ref old);
            WriteDecodedNibble(nibble3, shift, filter, ref older, ref old);
        }

        private void WriteDecodedNibble(sbyte nibble, byte shift, byte filter, ref short older, ref short old)
        {
            short shifted = ApplyBrrShift(nibble, shift);
            short sample = ApplyBrrFilter(shifted, filter, old, older);
            BrrBuffer.Write(sample);
            older = old;
            old = sample;
        }

        private void ClockEnvelope(ushort globalCounter)
        {
            if (EnvelopePhase == EnvelopePhase.Release)
            {
                EnvelopeLevel = (ushort)Math.Max(0, EnvelopeLevel - 8);
                ClippedEnvelopeValue = (ushort)((EnvelopeLevel - 8) & 0x7FF);
                return;
            }

            if (EnvelopePhase == EnvelopePhase.Attack && EnvelopeLevel >= 0x7E0)
                EnvelopePhase = EnvelopePhase.Decay;

            if (EnvelopePhase == EnvelopePhase.Decay)
            {
                int sustain = (SustainLevel + 1) << 8;
                if (EnvelopeLevel <= sustain)
                    EnvelopePhase = EnvelopePhase.Sustain;
            }

            int current = EnvelopeLevel;
            int rate;
            int step;

            if (EnvelopeMode == EnvelopeMode.Gain && GainMode == GainMode.Direct)
            {
                int target = GainValue << 4;
                if (current == target)
                {
                    rate = 0; step = 0;
                }
                else
                {
                    rate = 31; step = target - current;
                }
            }
            else if (EnvelopeMode == EnvelopeMode.Gain && GainMode == GainMode.Custom)
            {
                rate = GainValue & 0x1F;
                int mode = GainValue & 0x60;
                step = mode switch
                {
                    0x00 => -32,
                    0x20 => ComputeExpDecay(current),
                    0x40 => 32,
                    0x60 => (ClippedEnvelopeValue < 0x600) ? 32 : 8,
                    _ => 0
                };
            }
            else
            {
                switch (EnvelopePhase)
                {
                    case EnvelopePhase.Attack:
                        rate = (AttackRate << 1) | 0x01;
                        step = rate == 31 ? 1024 : 32;
                        break;
                    case EnvelopePhase.Decay:
                        rate = 0x10 | (DecayRate << 1);
                        step = ComputeExpDecay(current);
                        break;
                    case EnvelopePhase.Sustain:
                        rate = SustainRate;
                        step = ComputeExpDecay(current);
                        break;
                    default:
                        rate = 31;
                        step = -8;
                        break;
                }
            }

            if (rate != 0 && IsEnvelopeTick(globalCounter, rate))
            {
                int newVal = current + step;
                if (newVal < 0) newVal = 0;
                if (newVal > 0x7FF) newVal = 0x7FF;
                EnvelopeLevel = (ushort)newVal;
                ClippedEnvelopeValue = (ushort)(newVal & 0x7FF);
            }
        }
    }

    private sealed class NoiseGenerator
    {
        public short Output = (short)(short.MinValue >> 1);

        public void Clock(ushort globalCounter, byte noiseFrequency)
        {
            int rate = noiseFrequency;
            if (rate != 0 && IsEnvelopeTick(globalCounter, rate))
            {
                int newBit = (Output & 1) ^ ((Output >> 1) & 1);
                Output = (short)(((Output >> 1) & 0x3FFF) | (newBit << 14));
                Output = (short)((Output << 1) >> 1);
            }
        }
    }

    private sealed class EchoFilter
    {
        public bool[] EchoEnabled = new bool[8];
        public ushort BufferStartAddress;
        public ushort BufferCurrentOffset;
        public ushort BufferSamplesRemaining = 1;
        public ushort BufferSizeSamples = 1;
        public sbyte VolumeL;
        public sbyte VolumeR;
        public sbyte FeedbackVolume;
        public sbyte[] FirCoefficients = new sbyte[8];
        private readonly short[] _sampleBufferL = new short[8];
        private readonly short[] _sampleBufferR = new short[8];
        private int _sampleBufferIdx;
        public byte LastEdlWrite;

        public void Reset()
        {
            Array.Clear(EchoEnabled);
            BufferStartAddress = 0;
            BufferCurrentOffset = 0;
            BufferSamplesRemaining = 1;
            BufferSizeSamples = 1;
            VolumeL = 0;
            VolumeR = 0;
            FeedbackVolume = 0;
            Array.Clear(FirCoefficients);
            Array.Clear(_sampleBufferL);
            Array.Clear(_sampleBufferR);
            _sampleBufferIdx = 0;
            LastEdlWrite = 0;
        }

        public void WriteEchoEnabled(byte eon)
        {
            for (int i = 0; i < 8; i++) EchoEnabled[i] = (eon & (1 << i)) != 0;
        }

        public byte ReadEchoEnabled()
        {
            int v = 0;
            for (int i = 0; i < 8; i++) if (EchoEnabled[i]) v |= (1 << i);
            return (byte)v;
        }

        public void WriteEchoBufferSize(byte edl)
        {
            BufferSizeSamples = (edl & 0x0F) == 0 ? (ushort)1 : (ushort)(edl << 9);
            LastEdlWrite = edl;
        }

        public (int l, int r) DoFilter(bool echoWritesEnabled, byte[] audioRam, int[] voiceSamplesL, int[] voiceSamplesR)
        {
            ushort currentAddr = (ushort)(BufferStartAddress + BufferCurrentOffset);
            _sampleBufferL[_sampleBufferIdx] = ReadEchoSample(audioRam, currentAddr);
            _sampleBufferR[_sampleBufferIdx] = ReadEchoSample(audioRam, (ushort)(currentAddr + 2));

            int firL = 0;
            int firR = 0;
            for (int i = 0; i < 7; i++)
            {
                int coeff = FirCoefficients[i];
                int idx = (_sampleBufferIdx + i + 1) & 0x07;
                firL += (coeff * _sampleBufferL[idx]) >> 6;
                firR += (coeff * _sampleBufferR[idx]) >> 6;
            }
            firL = (short)firL;
            firR = (short)firR;
            firL += (FirCoefficients[7] * _sampleBufferL[_sampleBufferIdx]) >> 6;
            firR += (FirCoefficients[7] * _sampleBufferR[_sampleBufferIdx]) >> 6;
            firL = Clamp16(firL);
            firR = Clamp16(firR);
            firL &= ~1;
            firR &= ~1;

            if (echoWritesEnabled)
                WriteToEchoBuffer(audioRam, voiceSamplesL, voiceSamplesR, firL, firR);

            _sampleBufferIdx = (_sampleBufferIdx + 1) & 0x07;

            BufferSamplesRemaining--;
            if (BufferSamplesRemaining == 0)
            {
                BufferCurrentOffset = 0;
                BufferSamplesRemaining = BufferSizeSamples;
            }
            else
            {
                BufferCurrentOffset += 4;
            }

            int echoOutL = (firL * VolumeL) >> 7;
            int echoOutR = (firR * VolumeR) >> 7;
            return (echoOutL, echoOutR);
        }

        private void WriteToEchoBuffer(byte[] audioRam, int[] voiceSamplesL, int[] voiceSamplesR, int firL, int firR)
        {
            int sumL = 0;
            int sumR = 0;
            for (int i = 0; i < 8; i++)
            {
                if (!EchoEnabled[i]) continue;
                sumL += voiceSamplesL[i];
                sumR += voiceSamplesR[i];
                sumL = Clamp16(sumL);
                sumR = Clamp16(sumR);
            }
            int feedbackL = (firL * FeedbackVolume) >> 7;
            int feedbackR = (firR * FeedbackVolume) >> 7;
            int echoSampleL = Clamp16(sumL + feedbackL) & ~1;
            int echoSampleR = Clamp16(sumR + feedbackR) & ~1;

            ushort currentAddr = (ushort)(BufferStartAddress + BufferCurrentOffset);
            WriteEchoSample(audioRam, currentAddr, (short)echoSampleL);
            WriteEchoSample(audioRam, (ushort)(currentAddr + 2), (short)echoSampleR);
        }

        private static short ReadEchoSample(byte[] audioRam, ushort address)
        {
            byte lsb = audioRam[address];
            byte msb = audioRam[(ushort)(address + 1)];
            short v = (short)(lsb | (msb << 8));
            return (short)(v >> 1);
        }

        private static void WriteEchoSample(byte[] audioRam, ushort address, short value)
        {
            byte lsb = (byte)(value & 0xFF);
            byte msb = (byte)((value >> 8) & 0xFF);
            audioRam[address] = lsb;
            audioRam[(ushort)(address + 1)] = msb;
        }
    }

    private sealed class DspRegisters
    {
        public ushort SampleTableAddress;
        public sbyte MasterVolumeL;
        public sbyte MasterVolumeR;
        public byte NoiseFrequency;
        public bool EchoBufferWritesEnabled;
        public bool MuteAmplifier;
        public bool SoftReset;
        public ushort GlobalCounter;
        public readonly byte[] UnusedXa = new byte[8];
        public readonly byte[] UnusedXb = new byte[8];
        public readonly byte[] UnusedXe = new byte[8];
        public byte Unused1d;

        public void WriteFlg(byte value)
        {
            NoiseFrequency = (byte)(value & 0x1F);
            EchoBufferWritesEnabled = (value & 0x20) == 0;
            MuteAmplifier = (value & 0x40) != 0;
            SoftReset = (value & 0x80) != 0;
        }

        public byte ReadFlg()
        {
            int v = NoiseFrequency;
            if (!EchoBufferWritesEnabled) v |= 0x20;
            if (MuteAmplifier) v |= 0x40;
            if (SoftReset) v |= 0x80;
            return (byte)v;
        }
    }

    private readonly Voice[] _voices = new Voice[8];
    private readonly int[] _voiceSamplesL = new int[8];
    private readonly int[] _voiceSamplesR = new int[8];
    private readonly DspRegisters _registers = new();
    private readonly NoiseGenerator _noise = new();
    private readonly EchoFilter _echo = new();
    private byte _registerAddress;

    public DSP()
    {
        for (int i = 0; i < 8; i++)
            _voices[i] = new Voice();
    }

    public void SetAPU(IAPU apu) => _apu = apu;

    public void Reset()
    {
        SamplesL = new float[534];
        SamplesR = new float[534];
        SampleOffset = 0;
        _registerAddress = 0;
        _registers.SampleTableAddress = 0;
        _registers.MasterVolumeL = 0;
        _registers.MasterVolumeR = 0;
        _registers.NoiseFrequency = 0;
        _registers.EchoBufferWritesEnabled = false;
        _registers.MuteAmplifier = true;
        _registers.SoftReset = true;
        _registers.GlobalCounter = 0;
        Array.Clear(_registers.UnusedXa);
        Array.Clear(_registers.UnusedXb);
        Array.Clear(_registers.UnusedXe);
        _registers.Unused1d = 0;
        _noise.Output = (short)(short.MinValue >> 1);
        _echo.Reset();
        for (int i = 0; i < 8; i++)
            _voices[i].Reset();
        ResetPerfCounters();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Cycle()
    {
        if (_apu == null) return;
        if (PerfStatsEnabled)
            PerfCycles++;
        var audioRam = _apu.RAM;
        var voices = _voices;
        var voiceSamplesL = _voiceSamplesL;
        var voiceSamplesR = _voiceSamplesR;
        var registers = _registers;

        registers.GlobalCounter = registers.GlobalCounter == 0 ? (ushort)0x77FF : (ushort)(registers.GlobalCounter - 1);
        _noise.Clock(registers.GlobalCounter, registers.NoiseFrequency);

        for (int i = 0; i < 8; i++)
        {
            short prev = i != 0 ? voices[i - 1].CurrentSample : (short)0;
            voices[i].Clock(registers, audioRam, prev, _noise.Output);
        }

        int sumL = 0;
        int sumR = 0;
        for (int i = 0; i < 8; i++)
        {
            int sL = (voices[i].CurrentSample * voices[i].VolumeL) >> 6;
            int sR = (voices[i].CurrentSample * voices[i].VolumeR) >> 6;
            voiceSamplesL[i] = sL;
            voiceSamplesR[i] = sR;
            sumL += sL;
            sumR += sR;
            sumL = Clamp16(sumL);
            sumR = Clamp16(sumR);
        }

        bool echoWritesEnabled = registers.EchoBufferWritesEnabled;
        var (echoL, echoR) = _echo.DoFilter(echoWritesEnabled, audioRam, voiceSamplesL, voiceSamplesR);
        if (echoWritesEnabled)
            if (PerfStatsEnabled)
                PerfEchoWrites++;

        sumL = (sumL * registers.MasterVolumeL) >> 7;
        sumR = (sumR * registers.MasterVolumeR) >> 7;
        sumL = Clamp16(sumL);
        sumR = Clamp16(sumR);

        int outL = sumL + echoL;
        int outR = sumR + echoR;
        outL = Clamp16(outL);
        outR = Clamp16(outR);

        if (registers.MuteAmplifier)
        {
            outL = 0;
            outR = 0;
        }

        if (SampleOffset < SamplesL.Length)
        {
            SamplesL[SampleOffset] = outL / 32768f;
            SamplesR[SampleOffset] = outR / 32768f;
            SampleOffset++;
            if (PerfStatsEnabled)
                PerfProducedSamples++;
        }
    }

    internal void ResetPerfCounters()
    {
        if (!PerfStatsEnabled)
            return;
        PerfCycles = 0;
        PerfProducedSamples = 0;
        PerfEchoWrites = 0;
    }

    public byte Read(int adr)
    {
        int address = adr & 0x7F;
        int voice = address >> 4;
        switch (address & 0x0F)
        {
            case 0x00: return (byte)_voices[voice].VolumeL;
            case 0x01: return (byte)_voices[voice].VolumeR;
            case 0x02: return (byte)(_voices[voice].SampleRate & 0xFF);
            case 0x03: return _voices[voice].LastPitchHWrite;
            case 0x04: return _voices[voice].InstrumentNumber;
            case 0x05: return _voices[voice].ReadAdsrLow();
            case 0x06: return _voices[voice].ReadAdsrHigh();
            case 0x07: return _voices[voice].ReadGain();
            case 0x08: return _voices[voice].ReadEnvelope();
            case 0x09: return _voices[voice].ReadOutput();
            case 0x0A: return _registers.UnusedXa[voice];
            case 0x0B: return _registers.UnusedXb[voice];
            case 0x0E: return _registers.UnusedXe[voice];
            case 0x0F: return (byte)_echo.FirCoefficients[voice];
            case 0x0C:
            case 0x0D:
                return address switch
                {
                    0x0C => (byte)_registers.MasterVolumeL,
                    0x1C => (byte)_registers.MasterVolumeR,
                    0x2C => (byte)_echo.VolumeL,
                    0x3C => (byte)_echo.VolumeR,
                    0x4C => (byte)BuildFlags(i => _voices[i].KeyedOn),
                    0x5C => (byte)BuildFlags(i => _voices[i].KeyedOff),
                    0x6C => _registers.ReadFlg(),
                    0x7C => (byte)BuildFlags(i => _voices[i].EndFlagSeen),
                    0x0D => (byte)_echo.FeedbackVolume,
                    0x1D => _registers.Unused1d,
                    0x2D => (byte)BuildFlags(i => i != 0 && _voices[i].PitchModulationEnabled),
                    0x3D => (byte)BuildFlags(i => _voices[i].OutputNoise),
                    0x4D => _echo.ReadEchoEnabled(),
                    0x5D => (byte)(_registers.SampleTableAddress >> 8),
                    0x6D => (byte)(_echo.BufferStartAddress >> 8),
                    0x7D => _echo.LastEdlWrite,
                    _ => 0
                };
            default:
                return 0;
        }
    }

    public void Write(int adr, byte value)
    {
        int address = adr & 0x7F;
        int voice = address >> 4;
        switch (address & 0x0F)
        {
            case 0x00: _voices[voice].VolumeL = (sbyte)value; break;
            case 0x01: _voices[voice].VolumeR = (sbyte)value; break;
            case 0x02: _voices[voice].WritePitchLow(value); break;
            case 0x03: _voices[voice].WritePitchHigh(value); break;
            case 0x04: _voices[voice].InstrumentNumber = value; break;
            case 0x05: _voices[voice].WriteAdsrLow(value); break;
            case 0x06: _voices[voice].WriteAdsrHigh(value); break;
            case 0x07: _voices[voice].WriteGain(value); break;
            case 0x08: _registers.UnusedXa[voice] = value; break;
            case 0x09: _registers.UnusedXb[voice] = value; break;
            case 0x0E: _registers.UnusedXe[voice] = value; break;
            case 0x0F: _echo.FirCoefficients[voice] = (sbyte)value; break;
            case 0x0C:
            case 0x0D:
                switch (address)
                {
                    case 0x0C: _registers.MasterVolumeL = (sbyte)value; break;
                    case 0x1C: _registers.MasterVolumeR = (sbyte)value; break;
                    case 0x2C: _echo.VolumeL = (sbyte)value; break;
                    case 0x3C: _echo.VolumeR = (sbyte)value; break;
                    case 0x4C:
                        for (int i = 0; i < 8; i++) _voices[i].WriteKeyOn((value & (1 << i)) != 0);
                        break;
                    case 0x5C:
                        for (int i = 0; i < 8; i++) _voices[i].WriteKeyOff((value & (1 << i)) != 0);
                        break;
                    case 0x6C:
                        _registers.WriteFlg(value);
                        if (_registers.SoftReset)
                        {
                            for (int i = 0; i < 8; i++) _voices[i].SoftReset();
                        }
                        break;
                    case 0x7C:
                        // ENDX is cleared on write
                        for (int i = 0; i < 8; i++) _voices[i].EndFlagSeen = false;
                        break;
                    case 0x0D:
                        _echo.FeedbackVolume = (sbyte)value; break;
                    case 0x1D:
                        _registers.Unused1d = value; break;
                    case 0x2D:
                        for (int i = 1; i < 8; i++) _voices[i].PitchModulationEnabled = (value & (1 << i)) != 0;
                        break;
                    case 0x3D:
                        for (int i = 0; i < 8; i++) _voices[i].OutputNoise = (value & (1 << i)) != 0;
                        break;
                    case 0x4D:
                        _echo.WriteEchoEnabled(value); break;
                    case 0x5D:
                        _registers.SampleTableAddress = (ushort)(value << 8); break;
                    case 0x6D:
                        _echo.BufferStartAddress = (ushort)(value << 8); break;
                    case 0x7D:
                        _echo.WriteEchoBufferSize(value); break;
                }
                break;
        }
    }

    private static int BuildFlags(Func<int, bool> pred)
    {
        int v = 0;
        for (int i = 0; i < 8; i++) if (pred(i)) v |= 1 << i;
        return v;
    }

    private static bool IsEnvelopeTick(ushort globalCounter, int rateIndex)
    {
        if (rateIndex <= 0) return false;
        int rate = EnvelopeRate[rateIndex];
        if (rate == 0 || rate == ushort.MaxValue) return false;
        int offset = EnvelopeOffset[rateIndex];
        return ((globalCounter + offset) % rate) == 0;
    }

    private static short ApplyBrrShift(sbyte nibble, byte shift)
    {
        return shift switch
        {
            0 => (short)(nibble >> 1),
            >= 1 and <= 12 => (short)(nibble << (shift - 1)),
            >= 13 and <= 15 => (short)(nibble < 0 ? -2048 : 0),
            _ => 0
        };
    }

    private static short ApplyBrrFilter(short sample, byte filter, short old, short older)
    {
        int s = sample;
        int o = old;
        int od = older;
        int filtered = filter switch
        {
            0 => s,
            1 => s + o + (-o >> 4),
            2 => s + (o << 1) + (-(3 * o) >> 5) - od + (od >> 4),
            3 => s + (o << 1) + (-(13 * o) >> 6) - od + ((3 * od) >> 4),
            _ => s
        };

        if (filtered > short.MaxValue) filtered = short.MaxValue;
        else if (filtered < short.MinValue) filtered = short.MinValue;
        short clamped = (short)filtered;
        return (short)((clamped << 1) >> 1);
    }

    private static int GaussianInterpolate(short oldest, short older, short old, short sample, int offset)
    {
        int outR = (Gaussian[0x0FF - offset] * oldest) >> 11;
        outR += (Gaussian[0x1FF - offset] * older) >> 11;
        outR += (Gaussian[0x100 + offset] * old) >> 11;
        short clipped = (short)((short)outR << 1 >> 1);
        int sum = clipped + ((Gaussian[offset] * sample) >> 11);
        if (sum > 0x3FFF) sum = 0x3FFF;
        else if (sum < -0x4000) sum = -0x4000;
        return sum;
    }

    private static int ComputeExpDecay(int currentValue)
    {
        return -(((currentValue - 1) >> 8) + 1);
    }
}
