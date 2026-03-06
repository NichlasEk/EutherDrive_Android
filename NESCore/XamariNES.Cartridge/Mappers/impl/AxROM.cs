using System;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Common.Extensions;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 7 (AxROM)
    /// </summary>
    public sealed class AxROM : MapperBase, IMapper
    {
        [NonSerialized] private readonly byte[] _prgRom;
        [NonSerialized] private readonly byte[] _chrRom;
        private readonly int _prgBankCount32k;
        private int _prgBank;

        public enumNametableMirroring NametableMirroring { get; set; }

        public AxROM(byte[] prgRom, byte[] chrRom)
        {
            _prgRom = prgRom;
            _chrRom = chrRom;
            _prgBankCount32k = Math.Max(1, _prgRom.Length / 0x8000);
            NametableMirroring = enumNametableMirroring.SingleLower;
        }

        public byte ReadByte(int offset)
        {
            if (offset < 0x2000)
                return _chrRom[offset % _chrRom.Length];

            if (offset <= 0x3FFF)
                return ReadInterceptors.TryGetValue(offset, out currentReadInterceptor)
                    ? currentReadInterceptor(offset)
                    : (byte)0x00;

            if (offset >= 0x8000)
            {
                int baseAddr = (_prgBank % _prgBankCount32k) * 0x8000;
                return _prgRom[baseAddr + (offset & 0x7FFF)];
            }

            return 0x00;
        }

        public void WriteByte(int offset, byte data)
        {
            if (offset < 0x2000)
            {
                _chrRom[offset % _chrRom.Length] = data;
                return;
            }

            if (offset <= 0x3FFF || offset == 0x4014)
            {
                if (WriteInterceptors.TryGetValue(offset, out currentWriteInterceptor))
                    currentWriteInterceptor(offset, data);
                return;
            }

            if (offset >= 0x8000)
            {
                _prgBank = data & 0x07;
                NametableMirroring = data.IsBitSet(4)
                    ? enumNametableMirroring.SingleUpper
                    : enumNametableMirroring.SingleLower;
            }
        }
    }
}
