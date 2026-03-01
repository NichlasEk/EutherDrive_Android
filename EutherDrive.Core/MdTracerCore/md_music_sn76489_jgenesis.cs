using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal sealed class JgSn76489
    {
        private static readonly bool AudioMuteFmPsg =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_MUTE_FMPSG"), "1", StringComparison.Ordinal);
        private enum WaveOutput : byte
        {
            Negative = 0,
            Positive = 1
        }

        private static WaveOutput Invert(WaveOutput value) => value == WaveOutput.Negative ? WaveOutput.Positive : WaveOutput.Negative;

        private sealed class SquareWaveGenerator
        {
            public ushort Counter = 1;
            public WaveOutput CurrentOutput = WaveOutput.Negative;
            public ushort Tone;
            public byte Attenuation = 0x0F;

            public void UpdateToneLowBits(byte data)
            {
                Tone = (ushort)((Tone & 0xFFF0) | (data & 0x0F));
            }

            public void UpdateToneHighBits(byte data)
            {
                Tone = (ushort)((Tone & 0x000F) | ((data & 0x3F) << 4));
            }

            public void Clock()
            {
                Counter--;
                if (Counter == 0)
                {
                    Counter = Tone == 0 ? (ushort)1 : Tone;
                    CurrentOutput = Invert(CurrentOutput);
                }
            }

            public double Sample(double[] volumeTable)
            {
                double output = CurrentOutput == WaveOutput.Positive ? 1.0 : -1.0;
                return output * volumeTable[Attenuation];
            }
        }

        private enum NoiseType : byte
        {
            Periodic,
            White
        }

        private enum NoiseReloadKind : byte
        {
            Value,
            Tone2
        }

        private struct NoiseReload
        {
            public NoiseReloadKind Kind;
            public ushort Value;

            public static NoiseReload FromNoiseRegister(byte value)
            {
                return (value & 0x03) switch
                {
                    0x00 => new NoiseReload { Kind = NoiseReloadKind.Value, Value = 0x10 },
                    0x01 => new NoiseReload { Kind = NoiseReloadKind.Value, Value = 0x20 },
                    0x02 => new NoiseReload { Kind = NoiseReloadKind.Value, Value = 0x40 },
                    0x03 => new NoiseReload { Kind = NoiseReloadKind.Tone2, Value = 0 },
                    _ => new NoiseReload { Kind = NoiseReloadKind.Value, Value = 0x10 }
                };
            }

            public ushort Resolve(ushort tone2)
            {
                if (Kind == NoiseReloadKind.Tone2)
                    return tone2 == 0 ? (ushort)1 : tone2;
                return Value;
            }
        }

        private sealed class NoiseGenerator
        {
            public ushort Counter;
            public WaveOutput CurrentCounterOutput = WaveOutput.Negative;
            public NoiseReload CounterReload = NoiseReload.FromNoiseRegister(0x00);
            public ushort Lfsr = InitialLfsr;
            public WaveOutput CurrentLfsrOutput = WaveOutput.Negative;
            public NoiseType NoiseType = NoiseType.Periodic;
            public byte Attenuation = 0x0F;

            public void WriteData(byte data)
            {
                CounterReload = NoiseReload.FromNoiseRegister(data);
                NoiseType = (data & 0x04) != 0 ? NoiseType.White : NoiseType.Periodic;
                Lfsr = InitialLfsr;
            }

            public void Clock(ushort tone2)
            {
                if (Counter > 0)
                    Counter--;
                if (Counter != 0)
                    return;

                Counter = CounterReload.Resolve(tone2);
                if (Counter == 0)
                    Counter = 1;
                CurrentCounterOutput = Invert(CurrentCounterOutput);
                if (CurrentCounterOutput == WaveOutput.Positive)
                {
                    ShiftLfsr();
                }
            }

            public double Sample(double[] volumeTable)
            {
                double output = CurrentLfsrOutput == WaveOutput.Positive ? 1.0 : -1.0;
                return output * volumeTable[Attenuation];
            }

            private void ShiftLfsr()
            {
                CurrentLfsrOutput = (Lfsr & 0x0001) != 0 ? WaveOutput.Positive : WaveOutput.Negative;
                int bit0 = (Lfsr & 0x0001) != 0 ? 1 : 0;
                int bit3 = (Lfsr & 0x0008) != 0 ? 1 : 0;
                int inputBit = NoiseType == NoiseType.White ? (bit0 ^ bit3) : bit0;
                Lfsr = (ushort)((Lfsr >> 1) | (inputBit << 15));
            }
        }

        private enum Register : byte
        {
            Tone0,
            Tone1,
            Tone2,
            Noise,
            Volume0,
            Volume1,
            Volume2,
            Volume3
        }

        private struct StereoControl
        {
            public bool Square0L;
            public bool Square1L;
            public bool Square2L;
            public bool NoiseL;
            public bool Square0R;
            public bool Square1R;
            public bool Square2R;
            public bool NoiseR;

            public void Reset()
            {
                Square0L = true;
                Square1L = true;
                Square2L = true;
                NoiseL = true;
                Square0R = true;
                Square1R = true;
                Square2R = true;
                NoiseR = true;
            }

            public void Write(byte value)
            {
                Square0R = (value & 0x01) != 0;
                Square1R = (value & 0x02) != 0;
                Square2R = (value & 0x04) != 0;
                NoiseR = (value & 0x08) != 0;
                Square0L = (value & 0x10) != 0;
                Square1L = (value & 0x20) != 0;
                Square2L = (value & 0x40) != 0;
                NoiseL = (value & 0x80) != 0;
            }
        }

        private const int SnDivider = 16;
        private static readonly int PsgSampling = ParseOutputSampleRate();
        private const ushort InitialLfsr = 0x8000;
        private const float PsgSoftclipScale = 12000f;
        private const float PsgDcBlockR = 0.995f;

        private static readonly double[] AttenuationToVolume = new[]
        {
            1.0,
            0.7943282347242815,
            0.6309573444801932,
            0.5011872336272722,
            0.3981071705534972,
            0.3162277660168379,
            0.25118864315095796,
            0.19952623149688792,
            0.15848931924611132,
            0.1258925411794167,
            0.1,
            0.07943282347242814,
            0.06309573444801932,
            0.05011872336272722,
            0.03981071705534972,
            0.0,
        };

        private readonly SquareWaveGenerator[] _square = { new SquareWaveGenerator(), new SquareWaveGenerator(), new SquareWaveGenerator() };
        private readonly NoiseGenerator _noise = new NoiseGenerator();
        private Register _latchedRegister = Register.Tone0;
        private StereoControl _stereo;
        private int _divider = SnDivider;
        private double _clockAccumulator;
        private double _systemSampleAccumulator;
        private double _psgTickSampleAccumulator;
        private float _dcBlockX1;
        private float _dcBlockY1;
        private short[] _ringBuffer = new short[32768];
        private int _ringRead;
        private int _ringWrite;
        private int _ringCount;
        private short _lastSample;

        public void Reset()
        {
            _latchedRegister = Register.Tone0;
            _divider = SnDivider;
            _clockAccumulator = 0;
            _systemSampleAccumulator = 0;
            _psgTickSampleAccumulator = 0;
            _dcBlockX1 = 0;
            _dcBlockY1 = 0;
            _ringRead = 0;
            _ringWrite = 0;
            _ringCount = 0;
            _lastSample = 0;
            _stereo.Reset();
            _square[0] = new SquareWaveGenerator();
            _square[1] = new SquareWaveGenerator();
            _square[2] = new SquareWaveGenerator();
            _noise.Counter = 0;
            _noise.CurrentCounterOutput = WaveOutput.Negative;
            _noise.CounterReload = NoiseReload.FromNoiseRegister(0x00);
            _noise.Lfsr = InitialLfsr;
            _noise.CurrentLfsrOutput = WaveOutput.Negative;
            _noise.NoiseType = NoiseType.Periodic;
            _noise.Attenuation = 0x0F;
        }

        public void AdvanceSystemCycles(long m68kCycles, int[] outVol, int noiseGainPercent)
        {
            if (m68kCycles <= 0)
                return;

            // md_main.SystemCycles are M68K cycles. Convert elapsed M68K cycles to
            // output samples at the configured output rate using active MD timing mode.
            double m68kClockHz = md_main.GetM68kClockHzFromTiming();
            _systemSampleAccumulator += (m68kCycles * PsgSampling) / m68kClockHz;
            int samples = (int)_systemSampleAccumulator;
            if (samples <= 0)
                return;
            _systemSampleAccumulator -= samples;

            for (int i = 0; i < samples; i++)
            {
                short sample = GenerateSampleWithInternalClockAdvance(outVol, noiseGainPercent);
                WriteRing(sample);
            }
        }

        public void AdvancePsgTicks(int psgTicks, int[] outVol, int noiseGainPercent)
        {
            if (psgTicks <= 0)
                return;

            // Explicit tick path: ticks are already in PSG clock domain.
            // Drive chip state per tick and emit output samples at configured output rate.
            double psgClockHz = GetPsgClockHz();
            double samplesPerTick = PsgSampling / psgClockHz;
            for (int i = 0; i < psgTicks; i++)
            {
                Tick();
                _psgTickSampleAccumulator += samplesPerTick;
                if (_psgTickSampleAccumulator < 1.0)
                    continue;

                int samplesToWrite = (int)_psgTickSampleAccumulator;
                _psgTickSampleAccumulator -= samplesToWrite;
                short sample = GenerateSampleCurrentState(outVol, noiseGainPercent);
                for (int s = 0; s < samplesToWrite; s++)
                    WriteRing(sample);
            }
        }

        public void Write(byte value)
        {
            if ((value & 0x80) != 0)
            {
                _latchedRegister = RegisterFromLatchByte(value);
                WriteRegisterLowBits(value);
            }
            else
            {
                WriteRegisterHighBits(value);
            }
        }

        public void WriteStereoControl(byte value)
        {
            _stereo.Write(value);
        }

        public int UpdateSample(int[] outVol, int noiseGainPercent)
        {
            _ = outVol;
            _ = noiseGainPercent;
            if (_ringCount > 0)
            {
                short sample = ReadRing();
                _lastSample = sample;
                return sample;
            }

            // Keep PSG time deterministic: when underflowing, do not synthesize
            // a new sample by advancing internal clocks out-of-band.
            return _lastSample;
        }

        private short GenerateSampleWithInternalClockAdvance(int[] outVol, int noiseGainPercent)
        {
            // Advance PSG clock based on output sample cadence.
            _clockAccumulator += GetPsgClockHz() / PsgSampling;
            int ticks = (int)_clockAccumulator;
            if (ticks > 0)
                _clockAccumulator -= ticks;
            for (int i = 0; i < ticks; i++)
                Tick();

            return GenerateSampleCurrentState(outVol, noiseGainPercent);
        }

        private short GenerateSampleCurrentState(int[] outVol, int noiseGainPercent)
        {
            double vol0 = _square[0].Sample(AttenuationToVolume) * GetOutVol(outVol, 6);
            double vol1 = _square[1].Sample(AttenuationToVolume) * GetOutVol(outVol, 7);
            double vol2 = _square[2].Sample(AttenuationToVolume) * GetOutVol(outVol, 8);
            double noise = _noise.Sample(AttenuationToVolume) * GetOutVol(outVol, 9);
            if (noiseGainPercent != 100)
                noise *= noiseGainPercent / 100.0;

            double sampleL = ((_stereo.Square0L ? vol0 : 0.0)
                              + (_stereo.Square1L ? vol1 : 0.0)
                              + (_stereo.Square2L ? vol2 : 0.0)
                              + (_stereo.NoiseL ? noise : 0.0)) / 4.0;

            if (AudioMuteFmPsg)
                return 0;

            float mixed = (float)sampleL;
            float dcBlocked = mixed - _dcBlockX1 + (PsgDcBlockR * _dcBlockY1);
            _dcBlockX1 = mixed;
            _dcBlockY1 = dcBlocked;
            float scaled = dcBlocked * PsgSoftclipScale;
            if (scaled > short.MaxValue) scaled = short.MaxValue;
            else if (scaled < short.MinValue) scaled = short.MinValue;
            return (short)MathF.Round(scaled);
        }

        private static int GetOutVol(int[] outVol, int index)
        {
            if (outVol == null || index < 0 || index >= outVol.Length)
                return 1;
            return outVol[index];
        }

        private int RingCapacity => _ringBuffer.Length;

        private void WriteRing(short sample)
        {
            if (RingCapacity == 0)
                return;

            _ringBuffer[_ringWrite] = sample;
            _ringWrite++;
            if (_ringWrite >= RingCapacity)
                _ringWrite = 0;

            if (_ringCount == RingCapacity)
            {
                _ringRead++;
                if (_ringRead >= RingCapacity)
                    _ringRead = 0;
            }
            else
            {
                _ringCount++;
            }
        }

        private short ReadRing()
        {
            if (_ringCount <= 0)
                return _lastSample;

            short sample = _ringBuffer[_ringRead];
            _ringRead++;
            if (_ringRead >= RingCapacity)
                _ringRead = 0;
            _ringCount--;
            return sample;
        }

        private static int ParseOutputSampleRate()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_OUTPUT_HZ");
            if (!string.IsNullOrWhiteSpace(raw)
                && int.TryParse(raw.Trim(), out int value)
                && value >= 22050
                && value <= 192000)
            {
                return value;
            }

            // Match adapter default to keep the chain synchronized.
            return 44100;
        }

        private static double GetPsgClockHz()
        {
            // SN76489 input clock in Genesis timing domain: mclk / 15.
            return md_main.GetGenesisMasterClockHz() / 15.0;
        }

        private void Tick()
        {
            _divider--;
            if (_divider != 0)
                return;

            _divider = SnDivider;
            _square[0].Clock();
            _square[1].Clock();
            _square[2].Clock();
            _noise.Clock(_square[2].Tone);
        }

        private void WriteRegisterLowBits(byte data)
        {
            switch (_latchedRegister)
            {
                case Register.Tone0:
                    _square[0].UpdateToneLowBits(data);
                    break;
                case Register.Tone1:
                    _square[1].UpdateToneLowBits(data);
                    break;
                case Register.Tone2:
                    _square[2].UpdateToneLowBits(data);
                    break;
                case Register.Noise:
                    _noise.WriteData(data);
                    break;
                case Register.Volume0:
                    _square[0].Attenuation = (byte)(data & 0x0F);
                    break;
                case Register.Volume1:
                    _square[1].Attenuation = (byte)(data & 0x0F);
                    break;
                case Register.Volume2:
                    _square[2].Attenuation = (byte)(data & 0x0F);
                    break;
                case Register.Volume3:
                    _noise.Attenuation = (byte)(data & 0x0F);
                    break;
            }
        }

        private void WriteRegisterHighBits(byte data)
        {
            switch (_latchedRegister)
            {
                case Register.Tone0:
                    _square[0].UpdateToneHighBits(data);
                    break;
                case Register.Tone1:
                    _square[1].UpdateToneHighBits(data);
                    break;
                case Register.Tone2:
                    _square[2].UpdateToneHighBits(data);
                    break;
                default:
                    WriteRegisterLowBits(data);
                    break;
            }
        }

        private static Register RegisterFromLatchByte(byte value)
        {
            return (value & 0x70) switch
            {
                0x00 => Register.Tone0,
                0x10 => Register.Volume0,
                0x20 => Register.Tone1,
                0x30 => Register.Volume1,
                0x40 => Register.Tone2,
                0x50 => Register.Volume2,
                0x60 => Register.Noise,
                0x70 => Register.Volume3,
                _ => Register.Tone0
            };
        }
    }
}
