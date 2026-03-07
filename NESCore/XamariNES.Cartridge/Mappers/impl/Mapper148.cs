using System;
using XamariNES.Cartridge.Mappers.Enums;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 148
    ///
    ///     Simple CNROM-like board with 8KB CHR banking.
    /// </summary>
    public class Mapper148 : MapperBase, IMapper
    {
        [NonSerialized]
        private readonly byte[] _prgRom;

        [NonSerialized]
        private readonly byte[] _chrRom;

        private readonly bool _useChrRam;
        private readonly int _chrBankCount8k;
        private int _chrBankOffset;

        public enumNametableMirroring NametableMirroring { get; set; }

        public Mapper148(byte[] prgRom, byte[] chrRom, bool useChrRam, enumNametableMirroring nametableMirroring)
        {
            _prgRom = prgRom;
            _chrRom = chrRom;
            _useChrRam = useChrRam;
            _chrBankCount8k = Math.Max(1, _chrRom.Length / 0x2000);
            NametableMirroring = nametableMirroring;
        }

        public byte ReadByte(int offset)
        {
            offset &= 0xFFFF;

            if (offset < 0x2000)
                return _chrRom[_chrBankOffset + offset];

            if (offset <= 0x3FFF)
                return ReadInterceptors.TryGetValue(offset, out currentReadInterceptor)
                    ? currentReadInterceptor(offset)
                    : (byte)0x00;

            if (offset >= 0x8000)
                return _prgRom[(offset - 0x8000) % _prgRom.Length];

            if (offset >= 0x4020)
                return 0x00;

            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Maximum value of offset is 0xFFFF");
        }

        public void WriteByte(int offset, byte data)
        {
            offset &= 0xFFFF;

            if (offset < 0x2000)
            {
                if (!_useChrRam)
                    return;
                _chrRom[_chrBankOffset + offset] = data;
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
                int bank = (data & 0x07) % _chrBankCount8k;
                _chrBankOffset = bank * 0x2000;
                return;
            }
        }
    }
}
