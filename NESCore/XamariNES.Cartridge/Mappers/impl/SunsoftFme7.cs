using System;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Common.Extensions;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 69 (Sunsoft FME-7 / Sunsoft-5B)
    ///
    ///     More Info: https://www.nesdev.org/wiki/Sunsoft_FME-7
    /// </summary>
    public sealed class SunsoftFme7 : MapperBase, IMapper, IMapperIrqProvider, IMapperCpuTick,
        IExpansionAudioProvider, IMapperOpenBusRead, ISaveRamProvider
    {
        private enum PrgBank0Type
        {
            Rom,
            Ram
        }

        [NonSerialized]
        private readonly byte[] _prgRom;
        [NonSerialized]
        private readonly byte[] _chrRom;
        private readonly byte[] _prgRam;
        private readonly bool _useChrRam;
        private readonly bool _hasPrgRam;
        private readonly int _prgBankCount8k;
        private readonly int _chrBankCount1k;
        private bool _saveRamDirty;

        private readonly byte[] _prgBanks = new byte[4];
        private PrgBank0Type _prgBank0Type = PrgBank0Type.Rom;
        private bool _prgRamEnabled;
        private readonly byte[] _chrBanks = new byte[8];
        private byte _commandRegister;

        private bool _irqEnabled;
        private bool _irqCounterEnabled;
        private ushort _irqCounter;
        private bool _irqPending;

        private readonly Sunsoft5bAudio _audio = new Sunsoft5bAudio();

        public enumNametableMirroring NametableMirroring { get; set; }
        public bool IrqPending => _irqPending;
        public bool BatteryBacked { get; }
        public bool IsSaveRamDirty => _saveRamDirty;

        public SunsoftFme7(byte[] prgRom, byte[] chrRom, bool useChrRam, int prgRamSize, bool batteryBacked,
            enumNametableMirroring mirroring = enumNametableMirroring.Vertical)
        {
            _prgRom = prgRom;
            _chrRom = chrRom;
            _useChrRam = useChrRam;
            _prgRam = new byte[Math.Max(1, prgRamSize)];
            _hasPrgRam = prgRamSize > 0;
            _prgBankCount8k = Math.Max(1, _prgRom.Length / 0x2000);
            _chrBankCount1k = Math.Max(1, _chrRom.Length / 0x0400);
            BatteryBacked = batteryBacked;
            NametableMirroring = mirroring;
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
                int chrIndex = ResolveChrAddress(offset);
                return _chrRom[chrIndex];
            }

            if (offset <= 0x3FFF)
                return ReadInterceptors.TryGetValue(offset, out currentReadInterceptor)
                    ? currentReadInterceptor(offset)
                    : (byte)0x00;

            if (offset >= 0x4020 && offset <= 0x5FFF)
                return cpuOpenBus;

            if (offset >= 0x6000 && offset <= 0x7FFF)
            {
                if (_prgBank0Type == PrgBank0Type.Ram)
                {
                    if (_prgRamEnabled && _hasPrgRam)
                        return _prgRam[ResolvePrgRamAddress(_prgBanks[0], offset)];
                    return cpuOpenBus;
                }

                return _prgRom[ResolvePrgRomAddress(_prgBanks[0], offset)];
            }

            if (offset >= 0x8000 && offset <= 0xDFFF)
            {
                int bankIndex = (offset - 0x6000) / 0x2000;
                return _prgRom[ResolvePrgRomAddress(_prgBanks[bankIndex], offset)];
            }

            if (offset >= 0xE000 && offset <= 0xFFFF)
            {
                int lastBank = _prgBankCount8k - 1;
                return _prgRom[(lastBank * 0x2000) + (offset & 0x1FFF)];
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
                if (_prgBank0Type == PrgBank0Type.Ram && _prgRamEnabled && _hasPrgRam)
                {
                    int ramAddress = ResolvePrgRamAddress(_prgBanks[0], offset);
                    if (_prgRam[ramAddress] != data)
                    {
                        _prgRam[ramAddress] = data;
                        _saveRamDirty = true;
                    }
                }
                return;
            }

            if (offset >= 0x8000 && offset <= 0x9FFF)
            {
                _commandRegister = (byte)(data & 0x0F);
                return;
            }

            if (offset >= 0xA000 && offset <= 0xBFFF)
            {
                WriteCommandValue(data);
                return;
            }

            if (offset >= 0xC000 && offset <= 0xDFFF)
            {
                _audio.HandleRegisterSelect(data);
                return;
            }

            if (offset >= 0xE000 && offset <= 0xFFFF)
            {
                _audio.HandleRegisterWrite(data);
                return;
            }
        }

        private void WriteCommandValue(byte data)
        {
            if (_commandRegister <= 0x07)
            {
                _chrBanks[_commandRegister] = data;
                return;
            }

            switch (_commandRegister)
            {
                case 0x08:
                    _prgBanks[0] = (byte)(data & 0x3F);
                    _prgBank0Type = data.IsBitSet(6) ? PrgBank0Type.Ram : PrgBank0Type.Rom;
                    _prgRamEnabled = data.IsBitSet(7);
                    break;
                case 0x09:
                case 0x0A:
                case 0x0B:
                    _prgBanks[_commandRegister - 0x08] = (byte)(data & 0x3F);
                    break;
                case 0x0C:
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
                    break;
                case 0x0D:
                    _irqEnabled = data.IsBitSet(0);
                    _irqCounterEnabled = data.IsBitSet(7);
                    _irqPending = false;
                    break;
                case 0x0E:
                    _irqCounter = (ushort)((_irqCounter & 0xFF00) | data);
                    break;
                case 0x0F:
                    _irqCounter = (ushort)((_irqCounter & 0x00FF) | (data << 8));
                    break;
            }
        }

        private int ResolveChrAddress(int offset)
        {
            int bank = _chrBanks[(offset >> 10) & 0x07] % _chrBankCount1k;
            return (bank * 0x0400) + (offset & 0x03FF);
        }

        private int ResolvePrgRomAddress(int bank, int offset)
        {
            int mappedBank = bank % _prgBankCount8k;
            return (mappedBank * 0x2000) + (offset & 0x1FFF);
        }

        private int ResolvePrgRamAddress(int bank, int offset)
        {
            if (_prgRam.Length == 0)
                return 0;
            int absolute = (bank << 13) | (offset & 0x1FFF);
            return absolute % _prgRam.Length;
        }

        public void TickCpu(int cycles)
        {
            for (int i = 0; i < cycles; i++)
            {
                _audio.TickCpu();
                TickIrqCounter();
            }
        }

        private void TickIrqCounter()
        {
            if (!_irqCounterEnabled)
                return;

            if (_irqEnabled && _irqCounter == 0)
                _irqPending = true;

            _irqCounter--;
        }

        public double MixAudio(double apuSample)
        {
            if (!_audio.Enabled)
                return apuSample;

            double apu = apuSample * 2.0 - 1.0;
            double mixed = 0.7 * apu - _audio.Sample();
            if (mixed > 1.0) mixed = 1.0;
            if (mixed < -1.0) mixed = -1.0;
            return (mixed + 1.0) * 0.5;
        }

        private sealed class Sunsoft5bAudio
        {
            private byte _selectedRegister;
            private bool _writesEnabled;
            private readonly Sunsoft5bChannel _channel1 = new Sunsoft5bChannel();
            private readonly Sunsoft5bChannel _channel2 = new Sunsoft5bChannel();
            private readonly Sunsoft5bChannel _channel3 = new Sunsoft5bChannel();

            public bool Enabled =>
                _channel1.Enabled || _channel2.Enabled || _channel3.Enabled ||
                _channel1.Volume != 0 || _channel2.Volume != 0 || _channel3.Volume != 0;

            public void HandleRegisterSelect(byte value)
            {
                _selectedRegister = (byte)(value & 0x0F);
                _writesEnabled = (value & 0xF0) == 0x00;
            }

            public void HandleRegisterWrite(byte value)
            {
                if (!_writesEnabled)
                    return;

                switch (_selectedRegister)
                {
                    case 0x00:
                        _channel1.SetPeriodLow(value);
                        break;
                    case 0x01:
                        _channel1.SetPeriodHigh(value);
                        break;
                    case 0x02:
                        _channel2.SetPeriodLow(value);
                        break;
                    case 0x03:
                        _channel2.SetPeriodHigh(value);
                        break;
                    case 0x04:
                        _channel3.SetPeriodLow(value);
                        break;
                    case 0x05:
                        _channel3.SetPeriodHigh(value);
                        break;
                    case 0x07:
                        _channel3.Enabled = !value.IsBitSet(2);
                        _channel2.Enabled = !value.IsBitSet(1);
                        _channel1.Enabled = !value.IsBitSet(0);
                        break;
                    case 0x08:
                        _channel1.SetVolume(value);
                        break;
                    case 0x09:
                        _channel2.SetVolume(value);
                        break;
                    case 0x0A:
                        _channel3.SetVolume(value);
                        break;
                }
            }

            public void TickCpu()
            {
                _channel1.TickCpu();
                _channel2.TickCpu();
                _channel3.TickCpu();
            }

            public double Sample()
            {
                return (_channel1.Sample() + _channel2.Sample() + _channel3.Sample()) / 3.0;
            }
        }

        private sealed class Sunsoft5bChannel
        {
            // FME-7/5B clocks tone channels every 16 CPU cycles.
            private const int DividerReload = 16;
            private static readonly double[] DacLookup = BuildDacLookup();

            private bool _waveStep;
            private int _divider = DividerReload;
            private ushort _timer;
            private ushort _period;

            public bool Enabled { get; set; }
            public byte Volume { get; private set; }

            public void SetPeriodLow(byte value)
            {
                _period = (ushort)((_period & 0xFF00) | value);
            }

            public void SetPeriodHigh(byte value)
            {
                _period = (ushort)((_period & 0x00FF) | ((value & 0x0F) << 8));
            }

            public void SetVolume(byte value)
            {
                Volume = (byte)(value & 0x0F);
            }

            public void TickCpu()
            {
                _divider--;
                if (_divider == 0)
                {
                    _divider = DividerReload;
                    TickTone();
                }
            }

            public double Sample()
            {
                int linear = !Enabled ? Volume : (_waveStep ? Volume : 0);
                return DacLookup[linear];
            }

            private void TickTone()
            {
                _timer++;
                if (_timer >= _period)
                {
                    _timer = 0;
                    _waveStep = !_waveStep;
                }
            }

            private static double[] BuildDacLookup()
            {
                var table = new double[16];
                table[1] = 1.0;
                for (int i = 2; i < table.Length; i++)
                    table[i] = table[i - 1] * Math.Pow(10.0, 3.0 / 20.0);

                // Empirical tweak: volume 15 increase to match 5B output shape better.
                table[15] *= 0.72;

                double max = table[15];
                for (int i = 1; i < table.Length; i++)
                    table[i] /= max;

                return table;
            }
        }
    }
}
