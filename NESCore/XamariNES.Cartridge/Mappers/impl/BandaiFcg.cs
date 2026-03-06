using System;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Common.Extensions;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 16/153/159 (Bandai FCG / LZ93D50)
    /// </summary>
    public sealed class BandaiFcg : MapperBase, IMapper, IMapperIrqProvider, IMapperCpuTick, ISaveRamProvider, IMapperOpenBusRead
    {
        private enum MemoryVariant
        {
            None,
            Ram,
            X24C01,
            X24C02
        }

        private enum Variant
        {
            Fcg,
            Lz93D50,
            Unknown
        }

        [NonSerialized] private readonly byte[] _prgRom;
        [NonSerialized] private readonly byte[] _chrRom;
        private readonly bool _useChrRam;
        private readonly int _prgBankCount16k;
        private readonly int _chrBankCount1k;

        private readonly byte[] _prgRam;
        private readonly bool _hasPrgRam;

        private readonly Variant _variant;
        private readonly MemoryVariant _memoryVariant;

        private byte _prgBank;
        private byte _prgOuter256kBank;
        private readonly byte[] _chrBanks = new byte[8];
        private bool _ramEnabled;

        private ushort _irqCounter;
        private ushort _irqLatch;
        private bool _irqEnabled;

        private readonly X24C01Chip _eeprom01;
        private readonly X24C02Chip _eeprom02;

        private bool _saveRamDirty;

        public enumNametableMirroring NametableMirroring { get; set; }
        public bool IrqPending => _irqEnabled && _irqCounter == 0;
        public bool BatteryBacked { get; }
        public bool IsSaveRamDirty => _saveRamDirty || IsEepromDirty();

        public BandaiFcg(
            byte[] prgRom,
            byte[] chrRom,
            bool useChrRam,
            int prgRamSize,
            bool batteryBacked,
            enumNametableMirroring mirroring = enumNametableMirroring.Horizontal,
            int mapperNumber = 16,
            int subMapperNumber = 0)
        {
            _prgRom = prgRom;
            _chrRom = chrRom;
            _useChrRam = useChrRam;
            _prgBankCount16k = Math.Max(1, _prgRom.Length / 0x4000);
            _chrBankCount1k = Math.Max(1, _chrRom.Length / 0x0400);

            _prgRam = new byte[Math.Max(1, prgRamSize)];
            _hasPrgRam = prgRamSize > 0;

            (_variant, _memoryVariant) = DetectVariant(mapperNumber, subMapperNumber, prgRamSize);

            _eeprom01 = _memoryVariant == MemoryVariant.X24C01 ? new X24C01Chip() : null;
            _eeprom02 = (_memoryVariant == MemoryVariant.X24C02 || _variant == Variant.Unknown)
                ? new X24C02Chip()
                : null;

            NametableMirroring = mirroring;
            BatteryBacked = batteryBacked;

            for (int i = 0; i < _chrBanks.Length; i++)
                _chrBanks[i] = 0;
        }

        private static (Variant, MemoryVariant) DetectVariant(int mapperNumber, int subMapperNumber, int prgRamSize)
        {
            if (mapperNumber == 153)
                return (Variant.Lz93D50, MemoryVariant.Ram);
            if (mapperNumber == 159)
                return (Variant.Lz93D50, MemoryVariant.X24C01);
            if (mapperNumber != 16)
                return (Variant.Unknown, MemoryVariant.X24C02);

            if (subMapperNumber == 4)
                return (Variant.Fcg, MemoryVariant.None);
            if (subMapperNumber == 5)
                return (Variant.Lz93D50, prgRamSize > 0 ? MemoryVariant.X24C02 : MemoryVariant.None);

            return (Variant.Unknown, MemoryVariant.X24C02);
        }

        public byte[] GetSaveRam()
        {
            if (_memoryVariant == MemoryVariant.X24C01)
                return _eeprom01.Memory;
            if (_memoryVariant == MemoryVariant.X24C02 || _variant == Variant.Unknown)
                return _eeprom02.Memory;
            return _prgRam;
        }

        public void ClearSaveRamDirty()
        {
            _saveRamDirty = false;
            _eeprom01?.ClearDirty();
            _eeprom02?.ClearDirty();
        }

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
                switch (_memoryVariant)
                {
                    case MemoryVariant.None:
                        return cpuOpenBus;
                    case MemoryVariant.Ram:
                        if (_ramEnabled && _hasPrgRam)
                            return _prgRam[(offset - 0x6000) % _prgRam.Length];
                        return cpuOpenBus;
                    case MemoryVariant.X24C01:
                    case MemoryVariant.X24C02:
                        return EepromReadOpenBus(cpuOpenBus);
                }
            }

            if (offset >= 0x8000 && offset <= 0xBFFF)
            {
                int bank = _prgBank % _prgBankCount16k;
                int addr = (bank * 0x4000) + (offset & 0x3FFF);
                addr |= (_prgOuter256kBank << 18);
                return _prgRom[addr & (_prgRom.Length - 1)];
            }

            if (offset >= 0xC000)
            {
                int innerPrgLen = (_variant == Variant.Lz93D50 && _memoryVariant == MemoryVariant.Ram)
                    ? Math.Max(0x4000, _prgRom.Length / 2)
                    : _prgRom.Length;
                int lastBankBase = Math.Max(0, innerPrgLen - 0x4000);
                int addr = lastBankBase + (offset & 0x3FFF);
                addr |= (_prgOuter256kBank << 18);
                return _prgRom[addr & (_prgRom.Length - 1)];
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

            bool registerWrite =
                ((_variant == Variant.Fcg || _variant == Variant.Unknown) && offset >= 0x6000 && offset <= 0x7FFF)
                || ((_variant == Variant.Lz93D50 || _variant == Variant.Unknown) && offset >= 0x8000 && offset <= 0xFFFF);

            if (registerWrite)
            {
                WriteRegister(offset, data);
                return;
            }

            if (_variant == Variant.Lz93D50 && _memoryVariant == MemoryVariant.Ram && offset >= 0x6000 && offset <= 0x7FFF)
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
            }
        }

        private void WriteRegister(int offset, byte data)
        {
            int reg = offset & 0x000F;

            if (_variant == Variant.Lz93D50 && _memoryVariant == MemoryVariant.Ram && reg <= 0x03)
            {
                _prgOuter256kBank = (byte)(data & 0x01);
                return;
            }

            if (reg <= 0x07)
            {
                _chrBanks[reg] = data;
                return;
            }

            switch (reg)
            {
                case 0x08:
                    _prgBank = (byte)(data & 0x0F);
                    break;
                case 0x09:
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
                case 0x0A:
                    _irqEnabled = data.IsBitSet(0);
                    if (_variant == Variant.Lz93D50 || _variant == Variant.Unknown)
                        _irqCounter = _irqLatch;
                    break;
                case 0x0B:
                    if (_variant == Variant.Fcg)
                        _irqCounter = (ushort)((_irqCounter & 0xFF00) | data);
                    else
                        _irqLatch = (ushort)((_irqLatch & 0xFF00) | data);
                    break;
                case 0x0C:
                    if (_variant == Variant.Fcg)
                        _irqCounter = (ushort)((_irqCounter & 0x00FF) | (data << 8));
                    else
                        _irqLatch = (ushort)((_irqLatch & 0x00FF) | (data << 8));
                    break;
                case 0x0D:
                    if (_variant == Variant.Lz93D50 && _memoryVariant == MemoryVariant.Ram)
                    {
                        _ramEnabled = data.IsBitSet(5);
                    }
                    else
                    {
                        _eeprom01?.HandleWrite(data);
                        _eeprom02?.HandleWrite(data);
                    }
                    break;
            }
        }

        public void TickCpu(int cycles)
        {
            for (int i = 0; i < cycles; i++)
            {
                if (_irqEnabled)
                    _irqCounter = (ushort)Math.Max(0, _irqCounter - 1);
            }
        }

        private int ResolveChrAddress(int offset)
        {
            if (_variant == Variant.Lz93D50 && _memoryVariant == MemoryVariant.Ram)
                return offset & (_chrRom.Length - 1);

            int bank = _chrBanks[(offset >> 10) & 0x07] % _chrBankCount1k;
            return (bank * 0x0400) + (offset & 0x03FF);
        }

        private byte EepromReadOpenBus(byte cpuOpenBus)
        {
            bool bit = false;
            if (_memoryVariant == MemoryVariant.X24C01)
                bit = _eeprom01 != null && _eeprom01.ReadBit();
            else if (_memoryVariant == MemoryVariant.X24C02 || _variant == Variant.Unknown)
                bit = _eeprom02 != null && _eeprom02.ReadBit();

            return (byte)((cpuOpenBus & 0xEF) | (bit ? 0x10 : 0x00));
        }

        private bool IsEepromDirty()
        {
            return (_eeprom01 != null && _eeprom01.Dirty) || (_eeprom02 != null && _eeprom02.Dirty);
        }

        private sealed class X24C01Chip
        {
            private enum State
            {
                Stopped,
                Standby,
                ReceivingAddress,
                ReceivingData,
                SendingData
            }

            private readonly byte[] _memory = new byte[128];
            private State _state = State.Stopped;
            private byte _address;
            private byte _bitsReceived;
            private byte _bitsRemaining;
            private bool _lastData;
            private bool _lastClock;

            public bool Dirty { get; private set; } = true;
            public byte[] Memory => _memory;

            public bool ReadBit()
            {
                if (_state != State.SendingData || _bitsRemaining == 8)
                    return false;
                return (_memory[_address] & (1 << _bitsRemaining)) != 0;
            }

            public void HandleWrite(byte value)
            {
                bool data = value.IsBitSet(6);
                bool clock = value.IsBitSet(5);

                if (_lastClock && clock && data != _lastData)
                {
                    if (data) _state = State.Stopped;
                    else if (_state == State.Stopped) _state = State.Standby;
                }
                else if (!_lastClock && clock)
                {
                    Clock(data);
                }

                _lastData = data;
                _lastClock = clock;
            }

            public void ClearDirty() => Dirty = false;

            private void Clock(bool data)
            {
                switch (_state)
                {
                    case State.Standby:
                        _bitsReceived = (byte)(data ? 1 : 0);
                        _bitsRemaining = 7;
                        _state = State.ReceivingAddress;
                        break;
                    case State.ReceivingAddress:
                        if (_bitsRemaining > 0)
                        {
                            _bitsReceived = (byte)((_bitsReceived << 1) | (data ? 1 : 0));
                            _bitsRemaining--;
                        }
                        else
                        {
                            _address = (byte)(_bitsReceived >> 1);
                            if ((_bitsReceived & 1) != 0)
                            {
                                _bitsRemaining = 8;
                                _state = State.SendingData;
                            }
                            else
                            {
                                _bitsReceived = 0;
                                _bitsRemaining = 8;
                                _state = State.ReceivingData;
                            }
                        }
                        break;
                    case State.ReceivingData:
                        if (_bitsRemaining == 0)
                        {
                            _address = (byte)((_address & 0xFC) | ((_address + 1) & 0x03));
                            _bitsReceived = 0;
                            _bitsRemaining = 8;
                        }
                        else
                        {
                            _bitsReceived = (byte)((_bitsReceived << 1) | (data ? 1 : 0));
                            _bitsRemaining--;
                            if (_bitsRemaining == 0)
                            {
                                _memory[_address] = _bitsReceived;
                                Dirty = true;
                            }
                        }
                        break;
                    case State.SendingData:
                        if (_bitsRemaining > 0)
                        {
                            _bitsRemaining--;
                        }
                        else if (!data)
                        {
                            _address = (byte)((_address + 1) & 127);
                            _bitsRemaining = 8;
                        }
                        else
                        {
                            _state = State.Stopped;
                        }
                        break;
                }
            }
        }

        private sealed class X24C02Chip
        {
            private enum State
            {
                Stopped,
                Standby,
                ReceivingDeviceAddress,
                ReceivingWriteAddress,
                ReceivingData,
                SendingData
            }

            private readonly byte[] _memory = new byte[256];
            private State _state = State.Stopped;
            private byte _address;
            private byte _bitsReceived;
            private byte _bitsRemaining;
            private bool _lastData;
            private bool _lastClock;

            public bool Dirty { get; private set; } = true;
            public byte[] Memory => _memory;

            public bool ReadBit()
            {
                if (_state != State.SendingData || _bitsRemaining == 8)
                    return false;
                return (_memory[_address] & (1 << _bitsRemaining)) != 0;
            }

            public void HandleWrite(byte value)
            {
                bool data = value.IsBitSet(6);
                bool clock = value.IsBitSet(5);

                if (_lastClock && clock && data != _lastData)
                {
                    if (data)
                    {
                        _state = State.Stopped;
                    }
                    else
                    {
                        _state = State.Standby;
                    }
                }
                else if (!_lastClock && clock)
                {
                    Clock(data);
                }

                _lastData = data;
                _lastClock = clock;
            }

            public void ClearDirty() => Dirty = false;

            private void Clock(bool data)
            {
                switch (_state)
                {
                    case State.Standby:
                        _bitsReceived = (byte)(data ? 1 : 0);
                        _bitsRemaining = 7;
                        _state = State.ReceivingDeviceAddress;
                        break;
                    case State.ReceivingDeviceAddress:
                        if (_bitsRemaining > 0)
                        {
                            _bitsReceived = (byte)((_bitsReceived << 1) | (data ? 1 : 0));
                            _bitsRemaining--;
                        }
                        else if ((_bitsReceived & 1) != 0)
                        {
                            _bitsRemaining = 8;
                            _state = State.SendingData;
                        }
                        else
                        {
                            _bitsReceived = 0;
                            _bitsRemaining = 8;
                            _state = State.ReceivingWriteAddress;
                        }
                        break;
                    case State.ReceivingWriteAddress:
                        if (_bitsRemaining > 0)
                        {
                            _bitsReceived = (byte)((_bitsReceived << 1) | (data ? 1 : 0));
                            _bitsRemaining--;
                        }
                        else
                        {
                            _address = _bitsReceived;
                            _bitsReceived = 0;
                            _bitsRemaining = 8;
                            _state = State.ReceivingData;
                        }
                        break;
                    case State.ReceivingData:
                        if (_bitsRemaining > 0)
                        {
                            _bitsReceived = (byte)((_bitsReceived << 1) | (data ? 1 : 0));
                            _bitsRemaining--;
                            if (_bitsRemaining == 0)
                            {
                                _memory[_address] = _bitsReceived;
                                Dirty = true;
                            }
                        }
                        else
                        {
                            _address = (byte)((_address & 0xFC) | ((_address + 1) & 0x03));
                            _bitsReceived = 0;
                            _bitsRemaining = 8;
                        }
                        break;
                    case State.SendingData:
                        if (_bitsRemaining > 0)
                        {
                            _bitsRemaining--;
                        }
                        else if (!data)
                        {
                            _address++;
                            _bitsRemaining = 8;
                        }
                        else
                        {
                            _address++;
                            _state = State.Stopped;
                        }
                        break;
                }
            }
        }
    }
}
