using System;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Common.Extensions;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 21/22/23/25 (Konami VRC2/VRC4)
    /// </summary>
    public sealed class VRC4 : MapperBase, IMapper, IMapperIrqProvider, IMapperCpuTick, IMapperOpenBusRead
    {
        private enum ChipType
        {
            Vrc2,
            Vrc4
        }

        private enum SingleVariant
        {
            Vrc2a,
            Vrc2b,
            Vrc2c,
            Vrc4a,
            Vrc4b,
            Vrc4c,
            Vrc4d,
            Vrc4e,
            Vrc4f
        }

        private enum PrgMode
        {
            Mode0,
            Mode1
        }

        [NonSerialized] private readonly byte[] _prgRom;
        [NonSerialized] private readonly byte[] _chrRom;
        [NonSerialized] private readonly byte[] _prgRam;
        private readonly bool _useChrRam;
        private readonly int _prgBankCount8k;
        private readonly int _chrBankCount1k;

        private readonly SingleVariant[] _variants;
        private readonly ChipType _chipType;

        private PrgMode _prgMode;
        private byte _prgBank0;
        private byte _prgBank1;
        private readonly ushort[] _chrBanks = new ushort[8];
        private bool _ramEnabled;
        private byte _vrc2RamBit;
        private readonly KonamiIrqCounter _irq = new KonamiIrqCounter();

        public enumNametableMirroring NametableMirroring { get; set; }
        public bool IrqPending => _chipType == ChipType.Vrc4 && _irq.InterruptFlag;

        public VRC4(byte[] prgRom, byte[] chrRom, bool useChrRam, int prgRamSize, int mapperNumber, int subMapperNumber,
            enumNametableMirroring mirroring = enumNametableMirroring.Vertical)
        {
            _prgRom = prgRom;
            _chrRom = chrRom;
            _useChrRam = useChrRam;
            _prgRam = new byte[Math.Max(1, prgRamSize)];
            _prgBankCount8k = Math.Max(1, _prgRom.Length / 0x2000);
            _chrBankCount1k = Math.Max(1, _chrRom.Length / 0x0400);
            NametableMirroring = mirroring;

            _variants = ResolveVariants(mapperNumber, subMapperNumber);
            _chipType = VariantType(_variants[0]);
        }

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
                if (_chipType == ChipType.Vrc2)
                {
                    if (_prgRam.Length > 1)
                        return _prgRam[(offset - 0x6000) % _prgRam.Length];
                    if (offset < 0x7000)
                        return (byte)((cpuOpenBus & 0xFE) | _vrc2RamBit);
                    return cpuOpenBus;
                }

                if (_ramEnabled)
                {
                    if (_prgRam.Length >= 8192)
                        return _prgRam[(offset - 0x6000) & 0x1FFF];
                    if (_prgRam.Length >= 2048 && offset <= 0x6FFF)
                        return _prgRam[(offset - 0x6000) & 0x07FF];
                }
                return cpuOpenBus;
            }

            if (offset >= 0x8000 && offset <= 0x9FFF)
            {
                int bank = _prgMode == PrgMode.Mode0 ? _prgBank0 : (_prgBankCount8k - 2);
                return ReadPrg8k(bank, offset);
            }

            if (offset >= 0xA000 && offset <= 0xBFFF)
                return ReadPrg8k(_prgBank1, offset);

            if (offset >= 0xC000 && offset <= 0xDFFF)
            {
                int bank = _prgMode == PrgMode.Mode0 ? (_prgBankCount8k - 2) : _prgBank0;
                return ReadPrg8k(bank, offset);
            }

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
                if (_chipType == ChipType.Vrc2)
                {
                    if (_prgRam.Length > 1)
                        _prgRam[(offset - 0x6000) % _prgRam.Length] = data;
                    else if (offset < 0x7000)
                        _vrc2RamBit = (byte)(data & 0x01);
                }
                else if (_ramEnabled)
                {
                    if (_prgRam.Length >= 8192)
                        _prgRam[(offset - 0x6000) & 0x1FFF] = data;
                    else if (_prgRam.Length >= 2048 && offset <= 0x6FFF)
                        _prgRam[(offset - 0x6000) & 0x07FF] = data;
                }
                return;
            }

            if (offset >= 0x8000 && offset <= 0x8FFF)
            {
                _prgBank0 = (byte)(data & 0x1F);
                return;
            }

            if (offset >= 0x9000 && offset <= 0x9FFF)
            {
                int remapped = RemapAddress(offset) & 0x9003;
                if (_chipType == ChipType.Vrc2)
                {
                    if (remapped >= 0x9000 && remapped <= 0x9003)
                    {
                        NametableMirroring = data.IsBitSet(0)
                            ? enumNametableMirroring.Horizontal
                            : enumNametableMirroring.Vertical;
                    }
                }
                else
                {
                    if (remapped == 0x9000)
                    {
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
                    }
                    else if (remapped == 0x9002)
                    {
                        _ramEnabled = data.IsBitSet(0);
                        _prgMode = data.IsBitSet(1) ? PrgMode.Mode1 : PrgMode.Mode0;
                    }
                }
                return;
            }

            if (offset >= 0xA000 && offset <= 0xAFFF)
            {
                _prgBank1 = (byte)(data & 0x1F);
                return;
            }

            if (offset >= 0xB000 && offset <= 0xEFFF)
            {
                int remapped = RemapAddress(offset);
                int chrBankIndex = 2 * ((remapped - 0xB000) / 0x1000) + ((remapped & 0x0002) >> 1);
                ushort existing = _chrBanks[chrBankIndex];

                if ((remapped & 0x0001) == 0)
                {
                    if (_variants[0] == SingleVariant.Vrc2a)
                        _chrBanks[chrBankIndex] = (ushort)((existing & ~(0x0F >> 1)) | ((data & 0x0F) >> 1));
                    else
                        _chrBanks[chrBankIndex] = (ushort)((existing & ~0x0F) | (data & 0x0F));
                }
                else
                {
                    if (_variants[0] == SingleVariant.Vrc2a)
                    {
                        _chrBanks[chrBankIndex] = (ushort)((existing & 0x07) | ((data & 0x0F) << 3));
                    }
                    else if (_chipType == ChipType.Vrc2)
                    {
                        _chrBanks[chrBankIndex] = (ushort)((existing & 0x0F) | ((data & 0x0F) << 4));
                    }
                    else
                    {
                        _chrBanks[chrBankIndex] = (ushort)((existing & 0x0F) | ((data & 0x1F) << 4));
                    }
                }
                return;
            }

            if (_chipType == ChipType.Vrc4 && offset >= 0xF000)
            {
                switch (RemapAddress(offset) & 0xF003)
                {
                    case 0xF000:
                        _irq.SetReloadValueLow4Bits((byte)(data & 0x0F));
                        break;
                    case 0xF001:
                        _irq.SetReloadValueHigh4Bits((byte)(data & 0x0F));
                        break;
                    case 0xF002:
                        _irq.SetControl(data);
                        break;
                    case 0xF003:
                        _irq.Acknowledge();
                        break;
                }
            }
        }

        public void TickCpu(int cycles)
        {
            if (_chipType != ChipType.Vrc4)
                return;

            for (int i = 0; i < cycles; i++)
                _irq.TickCpu();
        }

        private byte ReadPrg8k(int bank, int offset)
        {
            int mapped = PositiveMod(bank, _prgBankCount8k);
            return _prgRom[(mapped * 0x2000) + (offset & 0x1FFF)];
        }

        private int RemapAddress(int address)
        {
            for (int i = 0; i < _variants.Length; i++)
            {
                if (TryRemap(_variants[i], address, out int remapped))
                    return remapped;
            }

            return address & 0xFF00;
        }

        private static bool TryRemap(SingleVariant variant, int address, out int remapped)
        {
            bool a0 = GetA0(variant, address);
            bool a1 = GetA1(variant, address);

            if (!a0 && !a1)
            {
                remapped = 0;
                return false;
            }

            remapped = (address & 0xFF00) | ((a1 ? 1 : 0) << 1) | (a0 ? 1 : 0);
            return true;
        }

        private static bool GetA0(SingleVariant variant, int address)
        {
            switch (variant)
            {
                case SingleVariant.Vrc2b:
                case SingleVariant.Vrc4f:
                    return (address & 0x0001) != 0;
                case SingleVariant.Vrc2a:
                case SingleVariant.Vrc2c:
                case SingleVariant.Vrc4a:
                case SingleVariant.Vrc4b:
                    return (address & 0x0002) != 0;
                case SingleVariant.Vrc4c:
                    return (address & 0x0040) != 0;
                case SingleVariant.Vrc4d:
                    return (address & 0x0008) != 0;
                case SingleVariant.Vrc4e:
                    return (address & 0x0004) != 0;
                default:
                    return false;
            }
        }

        private static bool GetA1(SingleVariant variant, int address)
        {
            switch (variant)
            {
                case SingleVariant.Vrc2b:
                case SingleVariant.Vrc4f:
                    return (address & 0x0002) != 0;
                case SingleVariant.Vrc2a:
                case SingleVariant.Vrc2c:
                case SingleVariant.Vrc4b:
                    return (address & 0x0001) != 0;
                case SingleVariant.Vrc4a:
                case SingleVariant.Vrc4d:
                    return (address & 0x0004) != 0;
                case SingleVariant.Vrc4c:
                    return (address & 0x0080) != 0;
                case SingleVariant.Vrc4e:
                    return (address & 0x0008) != 0;
                default:
                    return false;
            }
        }

        private static SingleVariant[] ResolveVariants(int mapperNumber, int subMapper)
        {
            switch (mapperNumber)
            {
                case 21:
                    if (subMapper == 1) return new[] { SingleVariant.Vrc4a };
                    if (subMapper == 2) return new[] { SingleVariant.Vrc4c };
                    return new[] { SingleVariant.Vrc4a, SingleVariant.Vrc4c };
                case 22:
                    return new[] { SingleVariant.Vrc2a };
                case 23:
                    if (subMapper == 1) return new[] { SingleVariant.Vrc4f };
                    if (subMapper == 2) return new[] { SingleVariant.Vrc4e };
                    if (subMapper == 3) return new[] { SingleVariant.Vrc2b };
                    return new[] { SingleVariant.Vrc4e, SingleVariant.Vrc4f };
                case 25:
                    if (subMapper == 1) return new[] { SingleVariant.Vrc4b };
                    if (subMapper == 2) return new[] { SingleVariant.Vrc4d };
                    if (subMapper == 3) return new[] { SingleVariant.Vrc2c };
                    return new[] { SingleVariant.Vrc4b, SingleVariant.Vrc4d };
                default:
                    throw new ArgumentException($"Unsupported VRC2/VRC4 mapper: {mapperNumber}");
            }
        }

        private static ChipType VariantType(SingleVariant variant)
        {
            switch (variant)
            {
                case SingleVariant.Vrc2a:
                case SingleVariant.Vrc2b:
                case SingleVariant.Vrc2c:
                    return ChipType.Vrc2;
                default:
                    return ChipType.Vrc4;
            }
        }

        private static int PositiveMod(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }
    }
}
