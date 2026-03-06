using System;
using XamariNES.Cartridge.Mappers.Enums;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 34 (BNROM / NINA-001)
    /// </summary>
    public sealed class BNROM : MapperBase, IMapper
    {
        [NonSerialized] private readonly byte[] _prgRom;
        [NonSerialized] private readonly byte[] _chrRom;
        [NonSerialized] private readonly byte[] _prgRam;
        private readonly bool _hasPrgRam;

        private readonly int _prgBankCount32k;
        private readonly int _chrBankCount4k;

        private int _prgBank;
        private int _chrBank0;
        private int _chrBank1 = 1;

        public enumNametableMirroring NametableMirroring { get; set; }

        public BNROM(byte[] prgRom, byte[] chrRom, int prgRamSize, enumNametableMirroring mirroring)
        {
            _prgRom = prgRom;
            _chrRom = chrRom;
            _prgRam = new byte[Math.Max(1, prgRamSize)];
            _hasPrgRam = prgRamSize > 0;
            _prgBankCount32k = Math.Max(1, _prgRom.Length / 0x8000);
            _chrBankCount4k = Math.Max(1, _chrRom.Length / 0x1000);
            NametableMirroring = mirroring;
        }

        public byte ReadByte(int offset)
        {
            if (offset < 0x1000)
            {
                int bank = _chrBank0 % _chrBankCount4k;
                return _chrRom[(bank * 0x1000) + (offset & 0x0FFF)];
            }

            if (offset < 0x2000)
            {
                int bank = _chrBank1 % _chrBankCount4k;
                return _chrRom[(bank * 0x1000) + (offset & 0x0FFF)];
            }

            if (offset <= 0x3FFF)
                return ReadInterceptors.TryGetValue(offset, out currentReadInterceptor)
                    ? currentReadInterceptor(offset)
                    : (byte)0x00;

            if (offset >= 0x6000 && offset <= 0x7FFF)
            {
                if (_hasPrgRam)
                    return _prgRam[(offset - 0x6000) % _prgRam.Length];
                return 0x00;
            }

            if (offset >= 0x8000)
            {
                int bank = _prgBank % _prgBankCount32k;
                return _prgRom[(bank * 0x8000) + (offset & 0x7FFF)];
            }

            return 0x00;
        }

        public void WriteByte(int offset, byte data)
        {
            if (offset < 0x1000)
            {
                int bank = _chrBank0 % _chrBankCount4k;
                _chrRom[(bank * 0x1000) + (offset & 0x0FFF)] = data;
                return;
            }

            if (offset < 0x2000)
            {
                int bank = _chrBank1 % _chrBankCount4k;
                _chrRom[(bank * 0x1000) + (offset & 0x0FFF)] = data;
                return;
            }

            if (offset <= 0x3FFF || offset == 0x4014)
            {
                if (WriteInterceptors.TryGetValue(offset, out currentWriteInterceptor))
                    currentWriteInterceptor(offset, data);
                return;
            }

            if (offset >= 0x6000 && offset <= 0x7FFC)
            {
                if (_hasPrgRam)
                    _prgRam[(offset - 0x6000) % _prgRam.Length] = data;
                return;
            }

            if (offset == 0x7FFE)
            {
                _chrBank0 = data & 0x0F;
                return;
            }

            if (offset == 0x7FFF)
            {
                _chrBank1 = data & 0x0F;
                return;
            }

            if (offset == 0x7FFD || offset >= 0x8000)
                _prgBank = data & 0x03;
        }
    }
}
