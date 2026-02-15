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
                double output = CurrentOutput == WaveOutput.Positive ? 1.0 : 0.0;
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
                return Kind == NoiseReloadKind.Tone2 ? tone2 : Value;
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
                CurrentCounterOutput = Invert(CurrentCounterOutput);
                if (CurrentCounterOutput == WaveOutput.Positive)
                {
                    ShiftLfsr();
                }
            }

            public double Sample(double[] volumeTable)
            {
                double output = CurrentLfsrOutput == WaveOutput.Positive ? 1.0 : 0.0;
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
        private const int PsgClock = 3579545;
        private const int PsgSampling = 44100;
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
        private float _dcBlockX1;
        private float _dcBlockY1;

        public void Reset()
        {
            _latchedRegister = Register.Tone0;
            _divider = SnDivider;
            _clockAccumulator = 0;
            _dcBlockX1 = 0;
            _dcBlockY1 = 0;
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
            // Advance PSG clock based on sample rate
            _clockAccumulator += (double)PsgClock / PsgSampling;
            int ticks = (int)_clockAccumulator;
            if (ticks > 0)
                _clockAccumulator -= ticks;
            for (int i = 0; i < ticks; i++)
                Tick();

            double vol0 = _square[0].Sample(AttenuationToVolume) * outVol[6];
            double vol1 = _square[1].Sample(AttenuationToVolume) * outVol[7];
            double vol2 = _square[2].Sample(AttenuationToVolume) * outVol[8];
            double noise = _noise.Sample(AttenuationToVolume) * outVol[9];
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
            return (int)MathF.Round(scaled);
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
