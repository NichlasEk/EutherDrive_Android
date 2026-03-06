using System;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Common.Extensions;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 85 (Konami VRC7)
    /// </summary>
    public sealed class VRC7 : MapperBase, IMapper, IMapperIrqProvider, IMapperCpuTick, IMapperOpenBusRead,
        IExpansionAudioProvider, ISaveRamProvider
    {
        private enum Variant
        {
            Vrc7a,
            Vrc7b,
            Unknown
        }

        [NonSerialized] private readonly byte[] _prgRom;
        [NonSerialized] private readonly byte[] _chrRom;
        private readonly byte[] _prgRam;
        private readonly bool _useChrRam;
        private readonly int _prgBankCount8k;
        private readonly int _chrBankCount1k;

        private readonly Variant _variant;
        private readonly KonamiIrqCounter _irq = new KonamiIrqCounter();

        private byte _prgBank0;
        private byte _prgBank1;
        private byte _prgBank2;
        private readonly byte[] _chrBanks = new byte[8];
        private bool _ramEnabled;

        private bool _audioEnabled;
        private byte _audioRegister;
        private readonly byte[] _audioRegs = new byte[0x40];
        private readonly Vrc7AudioUnit _audioUnit = new Vrc7AudioUnit();

        private bool _saveRamDirty;

        public enumNametableMirroring NametableMirroring { get; set; }
        public bool IrqPending => _irq.InterruptFlag;
        public bool BatteryBacked { get; }
        public bool IsSaveRamDirty => _saveRamDirty;

        public VRC7(byte[] prgRom, byte[] chrRom, bool useChrRam, int prgRamSize, bool batteryBacked,
            int subMapper, enumNametableMirroring mirroring = enumNametableMirroring.Vertical)
        {
            _prgRom = prgRom;
            _chrRom = chrRom;
            _useChrRam = useChrRam;
            _prgRam = new byte[Math.Max(1, prgRamSize)];
            _prgBankCount8k = Math.Max(1, _prgRom.Length / 0x2000);
            _chrBankCount1k = Math.Max(1, _chrRom.Length / 0x0400);
            _variant = subMapper == 1 ? Variant.Vrc7b : subMapper == 2 ? Variant.Vrc7a : Variant.Unknown;
            NametableMirroring = mirroring;
            BatteryBacked = batteryBacked;
        }

        public byte[] GetSaveRam() => _prgRam;

        public void ClearSaveRamDirty() => _saveRamDirty = false;

        public byte ReadByte(int offset)
        {
            return ReadByte(offset, 0x00);
        }

        public byte ReadByte(int offset, byte cpuOpenBus)
        {
            if (offset < 0x2000)
            {
                int bank = _chrBanks[(offset >> 10) & 0x07] % _chrBankCount1k;
                return _chrRom[(bank * 0x0400) + (offset & 0x03FF)];
            }

            if (offset <= 0x3FFF)
                return ReadInterceptors.TryGetValue(offset, out currentReadInterceptor)
                    ? currentReadInterceptor(offset)
                    : (byte)0x00;

            if (offset >= 0x4020 && offset <= 0x5FFF)
                return cpuOpenBus;

            if (offset >= 0x6000 && offset <= 0x7FFF)
            {
                if (_ramEnabled)
                    return _prgRam[(offset - 0x6000) % _prgRam.Length];
                return cpuOpenBus;
            }

            if (offset >= 0x8000 && offset <= 0x9FFF)
                return ReadPrg8k(_prgBank0, offset);
            if (offset >= 0xA000 && offset <= 0xBFFF)
                return ReadPrg8k(_prgBank1, offset);
            if (offset >= 0xC000 && offset <= 0xDFFF)
                return ReadPrg8k(_prgBank2, offset);
            if (offset >= 0xE000)
                return ReadPrg8k(_prgBankCount8k - 1, offset);

            return 0x00;
        }

        public void WriteByte(int offset, byte data)
        {
            if (offset < 0x2000)
            {
                if (!_useChrRam)
                    throw new AccessViolationException($"Invalid write to CHR ROM (CHR RAM not enabled). Offset: {offset:X4}");
                int bank = _chrBanks[(offset >> 10) & 0x07] % _chrBankCount1k;
                _chrRom[(bank * 0x0400) + (offset & 0x03FF)] = data;
                return;
            }

            if (offset <= 0x3FFF || offset == 0x4014)
            {
                if (WriteInterceptors.TryGetValue(offset, out currentWriteInterceptor))
                    currentWriteInterceptor(offset, data);
                return;
            }

            if (offset >= 0x6000 && offset <= 0x7FFF)
            {
                if (_ramEnabled)
                {
                    int idx = (offset - 0x6000) % _prgRam.Length;
                    if (_prgRam[idx] != data)
                    {
                        _prgRam[idx] = data;
                        _saveRamDirty = true;
                    }
                }
                return;
            }

            if (offset < 0x8000)
                return;

            switch (offset)
            {
                case 0x8000:
                    _prgBank0 = (byte)(data & 0x3F);
                    return;
                case 0x8010:
                    if (_variant == Variant.Vrc7a || _variant == Variant.Unknown)
                    {
                        _prgBank1 = (byte)(data & 0x3F);
                        return;
                    }
                    break;
                case 0x8008:
                    if (_variant == Variant.Vrc7b || _variant == Variant.Unknown)
                    {
                        _prgBank1 = (byte)(data & 0x3F);
                        return;
                    }
                    break;
                case 0x9000:
                    _prgBank2 = (byte)(data & 0x3F);
                    return;
                case 0x9010:
                    if (_variant == Variant.Vrc7a || _variant == Variant.Unknown)
                    {
                        _audioRegister = data;
                        return;
                    }
                    break;
                case 0x9030:
                    if (_variant == Variant.Vrc7a || _variant == Variant.Unknown)
                    {
                        if (_audioEnabled)
                        {
                            _audioRegs[_audioRegister & 0x3F] = data;
                            _audioUnit.WriteRegister(_audioRegister, data);
                        }
                        return;
                    }
                    break;
                case 0xE000:
                    switch (data & 0x03)
                    {
                        case 0x00:
                            NametableMirroring = enumNametableMirroring.Vertical;
                            break;
                        case 0x01:
                            NametableMirroring = enumNametableMirroring.Horizontal;
                            break;
                        case 0x02:
                            NametableMirroring = enumNametableMirroring.SingleLower;
                            break;
                        case 0x03:
                            NametableMirroring = enumNametableMirroring.SingleUpper;
                            break;
                    }
                    _ramEnabled = data.IsBitSet(7);
                    _audioEnabled = !data.IsBitSet(6);
                    if (!_audioEnabled)
                    {
                        Array.Clear(_audioRegs, 0, _audioRegs.Length);
                        _audioUnit.Reset();
                    }
                    return;
                case 0xE010:
                    if (_variant == Variant.Vrc7a || _variant == Variant.Unknown)
                    {
                        _irq.SetReloadValue(data);
                        return;
                    }
                    break;
                case 0xE008:
                    if (_variant == Variant.Vrc7b || _variant == Variant.Unknown)
                    {
                        _irq.SetReloadValue(data);
                        return;
                    }
                    break;
                case 0xF000:
                    _irq.SetControl(data);
                    return;
                case 0xF010:
                    if (_variant == Variant.Vrc7a || _variant == Variant.Unknown)
                    {
                        _irq.Acknowledge();
                        return;
                    }
                    break;
                case 0xF008:
                    if (_variant == Variant.Vrc7b || _variant == Variant.Unknown)
                    {
                        _irq.Acknowledge();
                        return;
                    }
                    break;
            }

            if (offset >= 0xA000 && offset <= 0xD010)
            {
                int addressMask = _variant == Variant.Vrc7a ? 0x0010 : _variant == Variant.Vrc7b ? 0x0008 : 0x0018;
                int chrBankIndex = 2 * ((offset - 0xA000) / 0x1000) + (((offset & addressMask) != 0) ? 1 : 0);
                if ((uint)chrBankIndex < 8)
                    _chrBanks[chrBankIndex] = data;
            }
        }

        public void TickCpu(int cycles)
        {
            for (int i = 0; i < cycles; i++)
            {
                _irq.TickCpu();
                _audioUnit.TickCpu();
            }
        }

        public double MixAudio(double apuSample)
        {
            if (!_audioEnabled)
                return apuSample;

            double vrc7Sample = _audioUnit.Sample();

            // Similar gain bias as jgenesis to avoid extremely low output.
            double amplified = Clamp(vrc7Sample * 1.5848931924611136, -1.0, 1.0);
            double apu = apuSample * 2.0 - 1.0;
            double mixed = Clamp(apu - amplified, -1.0, 1.0);
            return (mixed + 1.0) * 0.5;
        }

        private byte ReadPrg8k(int bank, int offset)
        {
            int mapped = PositiveMod(bank, _prgBankCount8k);
            return _prgRom[(mapped * 0x2000) + (offset & 0x1FFF)];
        }

        private static int PositiveMod(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private sealed class Vrc7AudioUnit
        {
            private const int CpuCyclesPerAudioClock = 36;
            private const double AudioClockHz = 1789772.7272727273 / CpuCyclesPerAudioClock;
            private static readonly InstrumentPatch[] BuiltInPatches = BuildPatchTable();

            private readonly byte[] _registers = new byte[0x40];
            private readonly ChannelState[] _channels = new ChannelState[6];
            private int _divider = CpuCyclesPerAudioClock;
            private double _sample;

            public Vrc7AudioUnit()
            {
                for (int i = 0; i < _channels.Length; i++)
                    _channels[i] = new ChannelState();
            }

            public void Reset()
            {
                Array.Clear(_registers, 0, _registers.Length);
                _divider = CpuCyclesPerAudioClock;
                _sample = 0.0;
                for (int i = 0; i < _channels.Length; i++)
                    _channels[i].Reset();
            }

            public void WriteRegister(byte register, byte value)
            {
                int reg = register & 0x3F;
                _registers[reg] = value;

                if (reg >= 0x20 && reg <= 0x25)
                {
                    int ch = reg - 0x20;
                    bool keyOn = (_registers[reg] & 0x10) != 0;
                    _channels[ch].SetKeyOn(keyOn);
                }
            }

            public void TickCpu()
            {
                _divider--;
                if (_divider > 0)
                    return;

                _divider = CpuCyclesPerAudioClock;
                _sample = TickAudioClock();
            }

            public double Sample() => _sample;

            private double TickAudioClock()
            {
                double mix = 0.0;
                for (int ch = 0; ch < 6; ch++)
                {
                    mix += ClockChannel(ch);
                }

                mix /= 6.0;
                return Clamp(mix, -1.0, 1.0);
            }

            private double ClockChannel(int ch)
            {
                int loReg = 0x10 + ch;
                int hiReg = 0x20 + ch;
                int volReg = 0x30 + ch;

                int fNum = _registers[loReg] | ((_registers[hiReg] & 0x01) << 8);
                int block = (_registers[hiReg] >> 1) & 0x07;
                bool sustain = (_registers[hiReg] & 0x20) != 0;
                int instrumentIndex = (_registers[volReg] >> 4) & 0x0F;
                int channelVolume = _registers[volReg] & 0x0F;

                InstrumentPatch patch = instrumentIndex == 0
                    ? InstrumentPatch.FromRegisters(_registers)
                    : BuiltInPatches[instrumentIndex - 1];

                ChannelState state = _channels[ch];

                // Lightweight frequency mapping that tracks OPLL pitch progression.
                double hz = (fNum + 1) * Math.Pow(2.0, block) * 0.172265625;
                double modMul = Math.Max(0.5, patch.ModMultiplier);
                double carMul = Math.Max(0.5, patch.CarMultiplier);

                state.ModPhase += (hz * modMul) / AudioClockHz;
                state.CarPhase += (hz * carMul) / AudioClockHz;
                state.ModPhase -= Math.Floor(state.ModPhase);
                state.CarPhase -= Math.Floor(state.CarPhase);

                state.ModEnv = AdvanceEnvelope(state.ModEnv, state.KeyOn, patch.ModAttack, patch.ModDecay, patch.ModRelease, patch.ModSustain, sustain);
                state.CarEnv = AdvanceEnvelope(state.CarEnv, state.KeyOn, patch.CarAttack, patch.CarDecay, patch.CarRelease, patch.CarSustain, sustain);

                double fb = patch.Feedback;
                double modInput = (2.0 * Math.PI * state.ModPhase) + (fb * state.LastModSample);
                double mod = Math.Sin(modInput) * state.ModEnv * patch.ModOutputLevel;
                state.LastModSample = mod;

                double carInput = (2.0 * Math.PI * state.CarPhase) + (mod * patch.ModToCarDepth);
                double car = Math.Sin(carInput) * state.CarEnv;

                double volumeScale = (15 - channelVolume) / 15.0;
                return Clamp(car * volumeScale, -1.0, 1.0);
            }

            private static double AdvanceEnvelope(
                double env,
                bool keyOn,
                double attackRate,
                double decayRate,
                double releaseRate,
                double sustainLevel,
                bool holdSustain)
            {
                if (keyOn)
                {
                    if (env < 1.0)
                    {
                        env += attackRate;
                        if (env > 1.0) env = 1.0;
                    }
                    else if (!holdSustain && env > sustainLevel)
                    {
                        env -= decayRate;
                        if (env < sustainLevel) env = sustainLevel;
                    }
                }
                else
                {
                    env -= releaseRate;
                    if (env < 0.0) env = 0.0;
                }

                return env;
            }

            private sealed class ChannelState
            {
                public double ModPhase;
                public double CarPhase;
                public double ModEnv;
                public double CarEnv;
                public double LastModSample;
                public bool KeyOn;

                public void SetKeyOn(bool keyOn)
                {
                    if (keyOn && !KeyOn)
                    {
                        ModEnv = 0.0;
                        CarEnv = 0.0;
                    }

                    KeyOn = keyOn;
                }

                public void Reset()
                {
                    ModPhase = 0.0;
                    CarPhase = 0.0;
                    ModEnv = 0.0;
                    CarEnv = 0.0;
                    LastModSample = 0.0;
                    KeyOn = false;
                }
            }

            private readonly struct InstrumentPatch
            {
                public readonly double ModMultiplier;
                public readonly double CarMultiplier;
                public readonly double ModOutputLevel;
                public readonly double ModToCarDepth;
                public readonly double Feedback;
                public readonly double ModAttack;
                public readonly double ModDecay;
                public readonly double ModRelease;
                public readonly double ModSustain;
                public readonly double CarAttack;
                public readonly double CarDecay;
                public readonly double CarRelease;
                public readonly double CarSustain;

                public InstrumentPatch(byte[] data)
                {
                    ModMultiplier = DecodeMultiplier(data[0] & 0x0F);
                    CarMultiplier = DecodeMultiplier(data[1] & 0x0F);
                    ModOutputLevel = 1.0 - ((data[2] & 0x3F) / 63.0);
                    Feedback = ((data[3] >> 1) & 0x07) * 0.35;
                    ModToCarDepth = 1.0 + ((data[3] & 0x07) * 0.22);

                    ModAttack = DecodeRate((data[4] >> 4) & 0x0F, 0.0018, 0.0300);
                    ModDecay = DecodeRate(data[4] & 0x0F, 0.00010, 0.0040);
                    CarAttack = DecodeRate((data[5] >> 4) & 0x0F, 0.0018, 0.0300);
                    CarDecay = DecodeRate(data[5] & 0x0F, 0.00010, 0.0040);

                    ModSustain = 1.0 - (((data[6] >> 4) & 0x0F) / 15.0);
                    CarSustain = 1.0 - (((data[7] >> 4) & 0x0F) / 15.0);
                    ModRelease = DecodeRate(data[6] & 0x0F, 0.00008, 0.0025);
                    CarRelease = DecodeRate(data[7] & 0x0F, 0.00008, 0.0025);
                }

                public static InstrumentPatch FromRegisters(byte[] registers)
                {
                    byte[] custom = new byte[8];
                    Array.Copy(registers, 0, custom, 0, 8);
                    return new InstrumentPatch(custom);
                }

                private static double DecodeMultiplier(int value)
                {
                    if (value == 0) return 0.5;
                    return value;
                }

                private static double DecodeRate(int nibble, double min, double max)
                {
                    return min + (max - min) * (nibble / 15.0);
                }
            }

            private static InstrumentPatch[] BuildPatchTable()
            {
                // YM2413/VRC7 built-in instrument bytes (15 entries), excluding custom instrument 0.
                byte[][] raw =
                {
                    new byte[] { 0x03, 0x21, 0x05, 0x06, 0x8B, 0x82, 0x42, 0x27 },
                    new byte[] { 0x13, 0x41, 0x14, 0x0D, 0xD8, 0xF6, 0x23, 0x12 },
                    new byte[] { 0x11, 0x11, 0x08, 0x08, 0xFA, 0xB2, 0x20, 0x12 },
                    new byte[] { 0x31, 0x61, 0x0C, 0x07, 0xA8, 0x64, 0x61, 0x27 },
                    new byte[] { 0x32, 0x21, 0x1E, 0x06, 0xE1, 0x76, 0x01, 0x28 },
                    new byte[] { 0x02, 0x01, 0x06, 0x00, 0xA3, 0xE2, 0xF4, 0xF4 },
                    new byte[] { 0x21, 0x61, 0x1D, 0x07, 0x82, 0x81, 0x11, 0x07 },
                    new byte[] { 0x23, 0x21, 0x22, 0x17, 0xA2, 0x72, 0x01, 0x17 },
                    new byte[] { 0x35, 0x11, 0x25, 0x00, 0x40, 0x73, 0x72, 0x01 },
                    new byte[] { 0xB5, 0x01, 0x0F, 0x0F, 0xA8, 0xA5, 0x51, 0x02 },
                    new byte[] { 0x17, 0xC1, 0x24, 0x07, 0xF8, 0xF7, 0x22, 0x12 },
                    new byte[] { 0x71, 0x23, 0x11, 0x06, 0x65, 0x74, 0x18, 0x16 },
                    new byte[] { 0x01, 0x02, 0xD3, 0x05, 0xC9, 0x95, 0x03, 0x02 },
                    new byte[] { 0x61, 0x63, 0x0C, 0x00, 0x94, 0xC0, 0x33, 0xF6 },
                    new byte[] { 0x21, 0x72, 0x0D, 0x00, 0xC1, 0xD5, 0x56, 0x06 },
                };

                InstrumentPatch[] patches = new InstrumentPatch[raw.Length];
                for (int i = 0; i < raw.Length; i++)
                    patches[i] = new InstrumentPatch(raw[i]);
                return patches;
            }
        }
    }
}
