using System;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Common.Extensions;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 24/26 (Konami VRC6)
    ///
    ///     More Info: https://www.nesdev.org/wiki/VRC6
    /// </summary>
    public sealed class VRC6 : MapperBase, IMapper, IMapperIrqProvider, ISaveRamProvider, IMapperCpuTick,
        IExpansionAudioProvider
    {
        [NonSerialized]
        private readonly byte[] _prgRom;
        [NonSerialized]
        private readonly byte[] _chrRom;
        private readonly byte[] _prgRam;
        private readonly bool _useChrRam;
        private readonly bool _hasPrgRam;
        private readonly int _prgBankCount8k;
        private readonly int _prgBankCount16k;
        private readonly int _chrBankCount1k;

        private readonly byte[] _chrBanks = new byte[8];
        private byte _prg16kBank;
        private byte _prg8kBank;
        private bool _ramEnabled;

        private readonly VrcIrqCounter _irq = new VrcIrqCounter();
        private readonly Vrc6PulseChannel _pulse1 = new Vrc6PulseChannel();
        private readonly Vrc6PulseChannel _pulse2 = new Vrc6PulseChannel();
        private readonly SawtoothChannel _sawtooth = new SawtoothChannel();

        private bool _saveRamDirty;
        public bool BatteryBacked { get; }

        private readonly Variant _variant;

        public enumNametableMirroring NametableMirroring { get; set; }

        public bool IrqPending => _irq.InterruptFlag;

        public VRC6(byte[] prgRom, byte[] chrRom, bool useChrRam, int prgRamSize, bool batteryBacked,
            int mapperNumber, enumNametableMirroring mirroring = enumNametableMirroring.Horizontal)
        {
            _prgRom = prgRom;
            _chrRom = chrRom;
            _useChrRam = useChrRam;
            _hasPrgRam = prgRamSize > 0;
            _prgRam = new byte[Math.Max(1, prgRamSize)];
            _prgBankCount8k = Math.Max(1, _prgRom.Length / 0x2000);
            _prgBankCount16k = Math.Max(1, _prgRom.Length / 0x4000);
            _chrBankCount1k = Math.Max(1, _chrRom.Length / 0x0400);
            BatteryBacked = batteryBacked;
            NametableMirroring = mirroring;
            _variant = mapperNumber == 26 ? Variant.Vrc6b : Variant.Vrc6a;

            _prg16kBank = 0;
            _prg8kBank = 2;
            for (int i = 0; i < _chrBanks.Length; i++)
                _chrBanks[i] = (byte)i;
        }

        public byte ReadByte(int offset)
        {
            if (offset < 0x2000)
            {
                int chrIndex = ResolveChrAddress(offset);
                return _chrRom[chrIndex];
            }

            if (offset <= 0x3FFF)
                return ReadInterceptors.TryGetValue(offset, out currentReadInterceptor)
                    ? currentReadInterceptor(offset)
                    : (byte)0x00;

            if (offset >= 0x6000 && offset <= 0x7FFF)
            {
                if (_ramEnabled && _hasPrgRam)
                    return _prgRam[(offset - 0x6000) % _prgRam.Length];
                return 0x00;
            }

            if (offset >= 0x8000 && offset <= 0xBFFF)
            {
                int bank = _prg16kBank % _prgBankCount16k;
                return _prgRom[(bank * 0x4000) + (offset - 0x8000)];
            }

            if (offset >= 0xC000 && offset <= 0xDFFF)
            {
                int bank = _prg8kBank % _prgBankCount8k;
                return _prgRom[(bank * 0x2000) + (offset - 0xC000)];
            }

            if (offset >= 0xE000 && offset <= 0xFFFF)
            {
                int lastBank = _prgBankCount8k - 1;
                return _prgRom[(lastBank * 0x2000) + (offset - 0xE000)];
            }

            return 0x00;
        }

        public void WriteByte(int offset, byte data)
        {
            if (offset < 0x2000)
            {
                if (!_useChrRam)
                    throw new AccessViolationException($"Invalid write to CHR ROM (CHR RAM not enabled). Offset: {offset:X4}");
                int chrIndex = ResolveChrAddress(offset);
                _chrRom[chrIndex] = data;
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
                if (_ramEnabled && _hasPrgRam)
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

            if (offset >= 0x8000 && offset <= 0xFFFF)
            {
                WriteRegister(offset, data);
                return;
            }
        }

        private void WriteRegister(int offset, byte data)
        {
            int remapped = RemapAddress(offset & 0xF003);
            switch (remapped)
            {
                case 0x8000:
                case 0x8001:
                case 0x8002:
                case 0x8003:
                    _prg16kBank = (byte)(data & 0x0F);
                    break;
                case 0x9000:
                    _pulse1.ProcessControlUpdate(data);
                    break;
                case 0x9001:
                    _pulse1.ProcessFreqLowUpdate(data);
                    break;
                case 0x9002:
                    _pulse1.ProcessFreqHighUpdate(data);
                    break;
                case 0xA000:
                    _pulse2.ProcessControlUpdate(data);
                    break;
                case 0xA001:
                    _pulse2.ProcessFreqLowUpdate(data);
                    break;
                case 0xA002:
                    _pulse2.ProcessFreqHighUpdate(data);
                    break;
                case 0xB000:
                    _sawtooth.ProcessControlUpdate(data);
                    break;
                case 0xB001:
                    _sawtooth.ProcessFreqLowUpdate(data);
                    break;
                case 0xB002:
                    _sawtooth.ProcessFreqHighUpdate(data);
                    break;
                case 0xB003:
                    switch (data & 0x0C)
                    {
                        case 0x00:
                            NametableMirroring = enumNametableMirroring.Vertical;
                            break;
                        case 0x04:
                            NametableMirroring = enumNametableMirroring.Horizontal;
                            break;
                        case 0x08:
                            NametableMirroring = enumNametableMirroring.SingleLower;
                            break;
                        case 0x0C:
                            NametableMirroring = enumNametableMirroring.SingleUpper;
                            break;
                    }
                    _ramEnabled = (data & 0x80) != 0;
                    break;
                case 0xC000:
                case 0xC001:
                case 0xC002:
                case 0xC003:
                    _prg8kBank = (byte)(data & 0x1F);
                    break;
                default:
                    if (remapped >= 0xD000 && remapped <= 0xE003)
                    {
                        int chrBankIndex = 4 * ((remapped - 0xD000) / 0x1000) + (remapped & 0x03);
                        if (chrBankIndex >= 0 && chrBankIndex < _chrBanks.Length)
                            _chrBanks[chrBankIndex] = data;
                    }
                    else if (remapped == 0xF000)
                    {
                        _irq.SetReloadValue(data);
                    }
                    else if (remapped == 0xF001)
                    {
                        _irq.SetControl(data);
                    }
                    else if (remapped == 0xF002)
                    {
                        _irq.Acknowledge();
                    }
                    break;
            }
        }

        private int ResolveChrAddress(int offset)
        {
            int bankIndex = (offset >> 10) & 0x07;
            int bankOffset = offset & 0x03FF;
            int bank = _chrBanks[bankIndex] % _chrBankCount1k;
            return (bank * 0x0400) + bankOffset;
        }

        private int RemapAddress(int address)
        {
            if (_variant == Variant.Vrc6a)
                return address;

            return (address & 0xFFFC) | ((address & 0x0001) << 1) | ((address & 0x0002) >> 1);
        }

        public void TickCpu(int cycles)
        {
            for (int i = 0; i < cycles; i++)
            {
                _irq.TickCpu();
                _pulse1.TickCpu();
                _pulse2.TickCpu();
                _sawtooth.TickCpu();
            }
        }

        public double MixAudio(double apuSample)
        {
            byte pulse1Sample = _pulse1.Sample();
            byte pulse2Sample = _pulse2.Sample();
            byte sawSample = _sawtooth.Sample();

            double vrc6Mix = (pulse1Sample + pulse2Sample + sawSample) / 61.0;
            double apu = apuSample * 2.0 - 1.0;
            double mixed = apu - 0.5255823148813802 * vrc6Mix;
            mixed *= 1.25;
            if (mixed > 1.0) mixed = 1.0;
            if (mixed < -1.0) mixed = -1.0;
            return (mixed + 1.0) * 0.5;
        }

        public bool IsSaveRamDirty => _saveRamDirty;

        public byte[] GetSaveRam() => _prgRam;

        public void ClearSaveRamDirty() => _saveRamDirty = false;

        private enum Variant
        {
            Vrc6a,
            Vrc6b
        }

        private sealed class Vrc6PulseChannel
        {
            private bool _enabled;
            private readonly PhaseTimer _timer = new PhaseTimer(16, 1, 12, false);
            private byte _volume;
            private byte _dutyCycle;

            public void ProcessControlUpdate(byte value)
            {
                _volume = (byte)(value & 0x0F);
                _dutyCycle = (byte)((value & 0x80) != 0 ? 15 : ((value & 0x70) >> 4));
            }

            public void ProcessFreqLowUpdate(byte value)
            {
                _timer.ProcessLoUpdate(value);
            }

            public void ProcessFreqHighUpdate(byte value)
            {
                _timer.ProcessHiUpdate(value);
                _enabled = value.IsBitSet(7);
                if (!_enabled)
                    _timer.Phase = 0;
            }

            public void TickCpu()
            {
                _timer.Tick(_enabled);
            }

            public byte Sample()
            {
                if (!_enabled)
                    return 0;

                byte dutyStep = (byte)((16 - _timer.Phase) & 0x0F);
                return dutyStep <= _dutyCycle ? _volume : (byte)0;
            }
        }

        private sealed class SawtoothChannel
        {
            private bool _enabled;
            private byte _accumulator;
            private byte _accumulatorClocks;
            private byte _accumulatorRate;
            private ushort _divider;
            private ushort _dividerPeriod;

            public void ProcessControlUpdate(byte value)
            {
                _accumulatorRate = (byte)(value & 0x3F);
            }

            public void ProcessFreqLowUpdate(byte value)
            {
                _dividerPeriod = (ushort)((_dividerPeriod & 0xFF00) | value);
            }

            public void ProcessFreqHighUpdate(byte value)
            {
                _dividerPeriod = (ushort)((_dividerPeriod & 0x00FF) | ((value & 0x0F) << 8));
                _enabled = value.IsBitSet(7);
                if (!_enabled)
                {
                    _accumulator = 0;
                    _accumulatorClocks = 0;
                }
            }

            public void TickCpu()
            {
                if (_divider == 0)
                {
                    _divider = _dividerPeriod;
                    if (_enabled)
                        ClockAccumulator();
                }
                else
                {
                    _divider--;
                }
            }

            public byte Sample()
            {
                return _enabled ? (byte)(_accumulator >> 3) : (byte)0;
            }

            private void ClockAccumulator()
            {
                _accumulatorClocks++;
                if (_accumulatorClocks == 14)
                {
                    _accumulator = 0;
                    _accumulatorClocks = 0;
                }
                else if ((_accumulatorClocks & 1) == 0)
                {
                    _accumulator += _accumulatorRate;
                }
            }
        }

        private sealed class PhaseTimer
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

        private sealed class VrcIrqCounter
        {
            private static readonly byte[] PrescalerSequence = { 114, 114, 113 };

            private byte _irqCounter;
            private byte _prescalerCounter;
            private int _prescalerSeqIndex;
            private bool _enabled;
            private bool _pending;
            private IrqMode _mode = IrqMode.Scanline;
            private byte _reloadValue;
            private bool _enableAfterAck;

            public bool InterruptFlag => _pending;

            public void SetReloadValue(byte value)
            {
                _reloadValue = value;
            }

            public void SetControl(byte value)
            {
                _pending = false;
                ResetPrescaler();

                _enableAfterAck = (value & 0x01) != 0;
                _enabled = (value & 0x02) != 0;
                _mode = (value & 0x04) != 0 ? IrqMode.Cycle : IrqMode.Scanline;

                if (_enabled)
                    _irqCounter = _reloadValue;
            }

            public void Acknowledge()
            {
                _pending = false;
                _enabled = _enableAfterAck;
            }

            public void TickCpu()
            {
                if (!_enabled)
                    return;

                if (_mode == IrqMode.Scanline)
                {
                    _prescalerCounter++;
                    if (_prescalerCounter == PrescalerSequence[_prescalerSeqIndex])
                    {
                        ClockIrq();
                        _prescalerCounter = 0;
                        _prescalerSeqIndex = (_prescalerSeqIndex + 1) % PrescalerSequence.Length;
                    }
                }
                else
                {
                    ClockIrq();
                }
            }

            private void ClockIrq()
            {
                if (_irqCounter == byte.MaxValue)
                {
                    _irqCounter = _reloadValue;
                    _pending = true;
                }
                else
                {
                    _irqCounter++;
                }
            }

            private void ResetPrescaler()
            {
                _prescalerCounter = 0;
                _prescalerSeqIndex = 0;
            }
        }

        private enum IrqMode
        {
            Scanline,
            Cycle
        }
    }
}
