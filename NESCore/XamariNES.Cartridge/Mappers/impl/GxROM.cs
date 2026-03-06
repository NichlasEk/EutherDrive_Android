using System;
using XamariNES.Cartridge.Mappers.Enums;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 11/66/140 (Color Dreams/GxROM/Jaleco JF-11/JF-14)
    /// </summary>
    public sealed class GxROM : MapperBase, IMapper
    {
        private enum Variant
        {
            Gxrom,
            ColorDreams,
            Jaleco
        }

        [NonSerialized] private readonly byte[] _prgRom;
        [NonSerialized] private readonly byte[] _chrRom;
        private readonly int _prgBankCount32k;
        private readonly int _chrBankCount8k;
        private readonly Variant _variant;

        private int _prgBank;
        private int _chrBank;

        public enumNametableMirroring NametableMirroring { get; set; }

        public GxROM(byte[] prgRom, byte[] chrRom, int mapperNumber, enumNametableMirroring mirroring)
        {
            _prgRom = prgRom;
            _chrRom = chrRom;
            _prgBankCount32k = Math.Max(1, _prgRom.Length / 0x8000);
            _chrBankCount8k = Math.Max(1, _chrRom.Length / 0x2000);
            NametableMirroring = mirroring;
            _variant = mapperNumber == 11 ? Variant.ColorDreams : mapperNumber == 140 ? Variant.Jaleco : Variant.Gxrom;
        }

        public byte ReadByte(int offset)
        {
            if (offset < 0x2000)
            {
                int bank = _chrBank % _chrBankCount8k;
                return _chrRom[(bank * 0x2000) + (offset & 0x1FFF)];
            }

            if (offset <= 0x3FFF)
                return ReadInterceptors.TryGetValue(offset, out currentReadInterceptor)
                    ? currentReadInterceptor(offset)
                    : (byte)0x00;

            if (offset >= 0x8000)
            {
                int bank = _prgBank % _prgBankCount32k;
                return _prgRom[(bank * 0x8000) + (offset & 0x7FFF)];
            }

            return 0x00;
        }

        public void WriteByte(int offset, byte data)
        {
            if (offset < 0x2000)
            {
                int bank = _chrBank % _chrBankCount8k;
                _chrRom[(bank * 0x2000) + (offset & 0x1FFF)] = data;
                return;
            }

            if (offset <= 0x3FFF || offset == 0x4014)
            {
                if (WriteInterceptors.TryGetValue(offset, out currentWriteInterceptor))
                    currentWriteInterceptor(offset, data);
                return;
            }

            bool registerWrite =
                (_variant == Variant.Jaleco && offset >= 0x6000 && offset <= 0x7FFF) ||
                (_variant != Variant.Jaleco && offset >= 0x8000);

            if (!registerWrite)
                return;

            int highNibble = (data >> 4) & 0x0F;
            int lowNibble = data & 0x0F;

            if (_variant == Variant.ColorDreams)
            {
                _prgBank = lowNibble;
                _chrBank = highNibble;
            }
            else
            {
                _prgBank = highNibble;
                _chrBank = lowNibble;
            }
        }
    }
}
