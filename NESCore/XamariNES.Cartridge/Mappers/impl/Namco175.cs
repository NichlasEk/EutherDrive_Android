using System;
using XamariNES.Cartridge.Mappers.Enums;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 210 (Namco 175 / Namco 340)
    /// </summary>
    public sealed class Namco175 : MapperBase, IMapper, IMapperOpenBusRead, ISaveRamProvider
    {
        private enum Variant
        {
            Namco175,
            Namco340,
            Unknown
        }

        [NonSerialized] private readonly byte[] _prgRom;
        [NonSerialized] private readonly byte[] _chrRom;
        private readonly byte[] _prgRam;
        private readonly bool _hasPrgRam;
        private readonly int _prgBankCount8k;
        private readonly int _chrBankCount1k;

        private readonly Variant _variant;
        private readonly byte[] _prgBanks = new byte[3];
        private readonly byte[] _chrBanks = new byte[8];
        private bool _ramEnabled;
        private bool _saveRamDirty;

        public enumNametableMirroring NametableMirroring { get; set; }
        public bool BatteryBacked { get; }
        public bool IsSaveRamDirty => _saveRamDirty;

        public Namco175(byte[] prgRom, byte[] chrRom, bool useChrRam, int prgRamSize, bool batteryBacked,
            int subMapper, enumNametableMirroring mirroring)
        {
            _ = useChrRam;
            _prgRom = prgRom;
            _chrRom = chrRom;
            _prgRam = new byte[Math.Max(1, prgRamSize)];
            _hasPrgRam = prgRamSize > 0;
            _prgBankCount8k = Math.Max(1, _prgRom.Length / 0x2000);
            _chrBankCount1k = Math.Max(1, _chrRom.Length / 0x0400);
            _variant = subMapper == 1 ? Variant.Namco175 : subMapper == 2 ? Variant.Namco340 : Variant.Unknown;
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
                if (_ramEnabled && _hasPrgRam)
                    return _prgRam[(offset - 0x6000) % _prgRam.Length];
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

            if (offset >= 0x8000 && offset <= 0xBFFF)
            {
                int bankIndex = (offset & 0x7FFF) / 0x0800;
                _chrBanks[bankIndex] = data;
                return;
            }

            if (offset >= 0xC000 && offset <= 0xC7FF)
            {
                if (_variant == Variant.Namco175 || _variant == Variant.Unknown)
                    _ramEnabled = (data & 0x01) != 0;
                return;
            }

            if (offset >= 0xE000 && offset <= 0xE7FF)
            {
                _prgBanks[0] = (byte)(data & 0x3F);
                if (_variant == Variant.Namco340 || _variant == Variant.Unknown)
                {
                    switch (data & 0xC0)
                    {
                        case 0x00:
                            NametableMirroring = enumNametableMirroring.SingleLower;
                            break;
                        case 0x40:
                            NametableMirroring = enumNametableMirroring.Vertical;
                            break;
                        case 0x80:
                            NametableMirroring = enumNametableMirroring.SingleUpper;
                            break;
                        case 0xC0:
                            NametableMirroring = enumNametableMirroring.Horizontal;
                            break;
                    }
                }
                return;
            }

            if (offset >= 0xE800 && offset <= 0xEFFF)
            {
                _prgBanks[1] = (byte)(data & 0x3F);
                return;
            }

            if (offset >= 0xF000 && offset <= 0xF7FF)
                _prgBanks[2] = (byte)(data & 0x3F);
        }
    }
}
