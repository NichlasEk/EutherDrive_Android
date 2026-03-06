using System;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Common.Extensions;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 19 (Namco 129 / Namco 163)
    /// </summary>
    public sealed class Namco163 : MapperBase, IMapper, IMapperOpenBusRead, IMapperIrqProvider,
        IMapperCpuTick, IExpansionAudioProvider, IPpuMemoryMapper, ISaveRamProvider
    {
        private const ushort MaxIrqCounter = 0x7FFF;
        private const int AudioDividerReload = 15;
        private const int ChannelMultiplexThreshold = 6;

        [NonSerialized] private readonly byte[] _prgRom;
        [NonSerialized] private readonly byte[] _chrMem;
        private readonly bool _useChrRam;
        private readonly byte[] _prgRam;
        private readonly bool _hasPrgRam;
        private readonly int _prgBankCount8k;
        private readonly int _chrBankCount1k;

        private readonly byte[] _internalRam = new byte[128];
        private byte _internalRamAddr;
        private bool _internalRamAutoIncrement;
        private bool _internalRamDirty = true;

        private readonly byte[] _prgBanks = new byte[3];
        private readonly byte[] _patternChrBanks = new byte[8];
        private readonly byte[] _nametableChrBanks = new byte[4];
        private readonly bool[] _vramChrBanksEnabled = new bool[2];

        private bool _ramWritesEnabled;
        private readonly bool[] _ramWindowWritesEnabled = new bool[4];

        private bool _irqEnabled;
        private ushort _irqCounter;

        private bool _audioEnabled;
        private readonly Namco163AudioChannel[] _channels = new Namco163AudioChannel[8];
        private int _audioDivider = AudioDividerReload;
        private int _currentChannel;
        private int _enabledChannelCount = 1;

        private readonly double _volumeCoefficient;
        private bool _saveRamDirty;

        public enumNametableMirroring NametableMirroring { get; set; }
        public bool IrqPending => _irqEnabled && _irqCounter == MaxIrqCounter;
        public bool BatteryBacked { get; }
        public bool IsSaveRamDirty => _saveRamDirty || _internalRamDirty;

        public Namco163(byte[] prgRom, byte[] chrMem, bool useChrRam, int prgRamSize, bool batteryBacked,
            int subMapper, enumNametableMirroring mirroring)
        {
            _prgRom = prgRom;
            _chrMem = chrMem;
            _useChrRam = useChrRam;
            _prgRam = new byte[Math.Max(1, prgRamSize)];
            _hasPrgRam = prgRamSize > 0;
            _prgBankCount8k = Math.Max(1, _prgRom.Length / 0x2000);
            _chrBankCount1k = Math.Max(1, _chrMem.Length / 0x0400);
            BatteryBacked = batteryBacked;
            NametableMirroring = mirroring;

            _volumeCoefficient = subMapper == 4 ? 0.5324537998876507
                : subMapper == 5 ? 0.6898933055568182
                : 0.31716257177124485;

            for (int i = 0; i < _channels.Length; i++)
                _channels[i] = new Namco163AudioChannel(i);
        }

        public byte[] GetSaveRam()
        {
            if (BatteryBacked && !_hasPrgRam)
                return _internalRam;
            return _prgRam;
        }

        public void ClearSaveRamDirty()
        {
            _saveRamDirty = false;
            _internalRamDirty = false;
        }

        public byte ReadByte(int offset)
        {
            return ReadByte(offset, 0x00);
        }

        public byte ReadByte(int offset, byte cpuOpenBus)
        {
            if (offset < 0x2000)
                return ReadChrMapped(offset, null);

            if (offset <= 0x3FFF)
                return ReadInterceptors.TryGetValue(offset, out currentReadInterceptor)
                    ? currentReadInterceptor(offset)
                    : (byte)0x00;

            if (offset >= 0x4020 && offset <= 0x47FF)
                return cpuOpenBus;

            if (offset >= 0x4800 && offset <= 0x4FFF)
            {
                byte value = _internalRam[_internalRamAddr];
                if (_internalRamAutoIncrement)
                    _internalRamAddr = (byte)((_internalRamAddr + 1) & 0x7F);
                return value;
            }

            if (offset >= 0x5000 && offset <= 0x57FF)
                return (byte)(_irqCounter & 0xFF);

            if (offset >= 0x5800 && offset <= 0x5FFF)
                return (byte)((_irqEnabled ? 0x80 : 0x00) | ((_irqCounter >> 8) & 0x7F));

            if (offset >= 0x6000 && offset <= 0x7FFF)
            {
                if (_hasPrgRam)
                    return _prgRam[(offset - 0x6000) & 0x1FFF];
                return cpuOpenBus;
            }

            if (offset >= 0x8000 && offset <= 0xDFFF)
            {
                int bankIndex = (offset & 0x7FFF) / 0x2000;
                int bank = _prgBanks[bankIndex] % _prgBankCount8k;
                return _prgRom[(bank * 0x2000) + (offset & 0x1FFF)];
            }

            if (offset >= 0xE000)
            {
                int bank = _prgBankCount8k - 1;
                return _prgRom[(bank * 0x2000) + (offset & 0x1FFF)];
            }

            return 0x00;
        }

        public void WriteByte(int offset, byte data)
        {
            if (offset < 0x2000)
            {
                WriteChrMapped(offset, data, null);
                return;
            }

            if (offset <= 0x3FFF || offset == 0x4014)
            {
                if (WriteInterceptors.TryGetValue(offset, out currentWriteInterceptor))
                    currentWriteInterceptor(offset, data);
                return;
            }

            if (offset >= 0x4020 && offset <= 0x47FF)
                return;

            if (offset >= 0x4800 && offset <= 0x4FFF)
            {
                byte ramAddr = _internalRamAddr;
                if (_internalRam[ramAddr] != data)
                {
                    _internalRam[ramAddr] = data;
                    _internalRamDirty = true;
                }

                if (ramAddr == 0x7F)
                    _enabledChannelCount = ((_internalRam[0x7F] & 0x70) >> 4) + 1;

                if (_internalRamAutoIncrement)
                    _internalRamAddr = (byte)((ramAddr + 1) & 0x7F);
                return;
            }

            if (offset >= 0x5000 && offset <= 0x57FF)
            {
                _irqCounter = (ushort)((_irqCounter & 0x7F00) | data);
                return;
            }

            if (offset >= 0x5800 && offset <= 0x5FFF)
            {
                _irqEnabled = data.IsBitSet(7);
                _irqCounter = (ushort)((_irqCounter & 0x00FF) | ((data & 0x7F) << 8));
                return;
            }

            if (offset >= 0x6000 && offset <= 0x7FFF)
            {
                if (_hasPrgRam && _ramWritesEnabled)
                {
                    int prgRamAddr = offset & 0x1FFF;
                    int window = prgRamAddr / 0x0800;
                    if (_ramWindowWritesEnabled[window])
                    {
                        int idx = prgRamAddr;
                        if (_prgRam[idx] != data)
                        {
                            _prgRam[idx] = data;
                            _saveRamDirty = true;
                        }
                    }
                }
                return;
            }

            if (offset >= 0x8000 && offset <= 0xBFFF)
            {
                int bankIndex = (offset & 0x7FFF) / 0x0800;
                _patternChrBanks[bankIndex] = data;
                return;
            }

            if (offset >= 0xC000 && offset <= 0xDFFF)
            {
                int bankIndex = (offset & 0x3FFF) / 0x0800;
                _nametableChrBanks[bankIndex] = data;
                return;
            }

            if (offset >= 0xE000 && offset <= 0xE7FF)
            {
                _audioEnabled = !data.IsBitSet(6);
                _prgBanks[0] = (byte)(data & 0x3F);
                return;
            }

            if (offset >= 0xE800 && offset <= 0xEFFF)
            {
                _vramChrBanksEnabled[1] = !data.IsBitSet(7);
                _vramChrBanksEnabled[0] = !data.IsBitSet(6);
                _prgBanks[1] = (byte)(data & 0x3F);
                return;
            }

            if (offset >= 0xF000 && offset <= 0xF7FF)
            {
                _prgBanks[2] = (byte)(data & 0x3F);
                return;
            }

            if (offset >= 0xF800)
            {
                _ramWritesEnabled = (data & 0xF0) == 0x40;
                for (int bit = 0; bit < 4; bit++)
                    _ramWindowWritesEnabled[bit] = !data.IsBitSet(bit);

                _internalRamAutoIncrement = data.IsBitSet(7);
                _internalRamAddr = (byte)(data & 0x7F);
            }
        }

        public byte ReadPpu(int address, byte[] vram)
        {
            return ReadChrMapped(address, vram);
        }

        public void WritePpu(int address, byte value, byte[] vram)
        {
            WriteChrMapped(address, value, vram);
        }

        public void TickCpu(int cycles)
        {
            for (int i = 0; i < cycles; i++)
            {
                if (_irqEnabled && _irqCounter < MaxIrqCounter)
                    _irqCounter++;

                TickAudio();
            }
        }

        public double MixAudio(double apuSample)
        {
            if (!_audioEnabled)
                return apuSample;

            double n163Sample = SampleAudio() * _volumeCoefficient;
            double mixed = apuSample + n163Sample;
            if (mixed > 1.0) mixed = 1.0;
            if (mixed < -1.0) mixed = -1.0;
            return mixed;
        }

        private void TickAudio()
        {
            _audioDivider--;
            if (_audioDivider != 0)
                return;

            _audioDivider = AudioDividerReload;

            _currentChannel = (_currentChannel - 1) & 0x07;
            if (_currentChannel < 8 - _enabledChannelCount)
                _currentChannel = 7;

            _channels[_currentChannel].Clock(_internalRam);
        }

        private double SampleAudio()
        {
            if (_enabledChannelCount < ChannelMultiplexThreshold)
                return _channels[_currentChannel].CurrentOutput;

            double sum = 0.0;
            for (int i = 0; i < _enabledChannelCount; i++)
            {
                int idx = 7 - i;
                sum += _channels[idx].CurrentOutput;
            }
            return sum / _enabledChannelCount;
        }

        private byte ReadChrMapped(int address, byte[] vram)
        {
            if (address <= 0x1FFF)
            {
                int bankIndex = (address >> 10) & 0x07;
                byte bankNumber = _patternChrBanks[bankIndex];
                int patternTableIndex = address / 0x1000;
                if (bankNumber >= 0xE0 && _vramChrBanksEnabled[patternTableIndex])
                {
                    if (vram == null)
                        return 0;
                    int vramAddr = ((bankNumber & 0x01) * 0x0400) | (address & 0x03FF);
                    return vram[vramAddr];
                }

                int chrAddr = ((bankNumber % _chrBankCount1k) * 0x0400) + (address & 0x03FF);
                return _chrMem[chrAddr];
            }

            if (address <= 0x3EFF)
            {
                int relativeAddr = address & 0x0FFF;
                int bankIndex = relativeAddr / 0x0400;
                byte bankNumber = _nametableChrBanks[bankIndex];
                if (bankNumber >= 0xE0)
                {
                    if (vram == null)
                        return 0;
                    int vramAddr = ((bankNumber & 0x01) * 0x0400) | (address & 0x03FF);
                    return vram[vramAddr];
                }

                int chrAddr = ((bankNumber % _chrBankCount1k) * 0x0400) + (address & 0x03FF);
                return _chrMem[chrAddr];
            }

            return 0;
        }

        private void WriteChrMapped(int address, byte value, byte[] vram)
        {
            if (address <= 0x1FFF)
            {
                int bankIndex = (address >> 10) & 0x07;
                byte bankNumber = _patternChrBanks[bankIndex];
                int patternTableIndex = address / 0x1000;
                if (bankNumber >= 0xE0 && _vramChrBanksEnabled[patternTableIndex])
                {
                    if (vram != null)
                    {
                        int vramAddr = ((bankNumber & 0x01) * 0x0400) | (address & 0x03FF);
                        vram[vramAddr] = value;
                    }
                    return;
                }

                if (_useChrRam)
                {
                    int chrAddr = ((bankNumber % _chrBankCount1k) * 0x0400) + (address & 0x03FF);
                    _chrMem[chrAddr] = value;
                }
                return;
            }

            if (address <= 0x3EFF)
            {
                int relativeAddr = address & 0x0FFF;
                int bankIndex = relativeAddr / 0x0400;
                byte bankNumber = _nametableChrBanks[bankIndex];
                if (bankNumber >= 0xE0)
                {
                    if (vram != null)
                    {
                        int vramAddr = ((bankNumber & 0x01) * 0x0400) | (address & 0x03FF);
                        vram[vramAddr] = value;
                    }
                    return;
                }

                if (_useChrRam)
                {
                    int chrAddr = ((bankNumber % _chrBankCount1k) * 0x0400) + (address & 0x03FF);
                    _chrMem[chrAddr] = value;
                }
            }
        }

        private sealed class Namco163AudioChannel
        {
            private readonly int _channelIndex;

            public Namco163AudioChannel(int channelIndex)
            {
                _channelIndex = channelIndex;
            }

            public double CurrentOutput { get; private set; }

            public void Clock(byte[] internalRam)
            {
                int configAddr = 0x40 | (8 * _channelIndex);

                int frequency = internalRam[configAddr]
                    | (internalRam[configAddr + 2] << 8)
                    | ((internalRam[configAddr + 4] & 0x03) << 16);

                int phase = internalRam[configAddr + 1]
                    | (internalRam[configAddr + 3] << 8)
                    | (internalRam[configAddr + 5] << 16);

                int length = 256 - (internalRam[configAddr + 4] & 0xFC);
                int baseAddress = internalRam[configAddr + 6];
                int volume = internalRam[configAddr + 7] & 0x0F;

                phase = (phase + frequency) & ((1 << 24) - 1);
                while (phase >= (length << 16))
                    phase -= (length << 16);

                int relativeSampleIndex = phase >> 16;
                int sampleIndex = (baseAddress + relativeSampleIndex) & 0xFF;
                byte sampleByte = internalRam[sampleIndex >> 1];
                int sample = (sampleByte >> (4 * (sampleIndex & 1))) & 0x0F;

                int centered = (sample - 8) * volume;
                CurrentOutput = centered / 120.0;

                internalRam[configAddr + 1] = (byte)(phase & 0xFF);
                internalRam[configAddr + 3] = (byte)((phase >> 8) & 0xFF);
                internalRam[configAddr + 5] = (byte)((phase >> 16) & 0xFF);
            }
        }
    }
}
