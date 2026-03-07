using System;
using XamariNES.Cartridge.Mappers.Enums;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 3 (CNROM)
    ///
    ///     More Info: https://wiki.nesdev.com/w/index.php/CNROM
    /// </summary>
    public class CNROM : MapperBase, IMapper
    {
        /// <summary>
        ///     PRG ROM
        ///
        ///     32KB Capacity
        /// </summary>
        [NonSerialized]
        private readonly byte[] _prgRom = new byte[0x8000];

        /// <summary>
        ///     CHR ROM
        ///
        ///     32KB Capacity, 8K Switchable Window
        /// </summary>
        [NonSerialized]
        private readonly byte[] _chrRom;
        private readonly int _chrBankCount;

        /// <summary>
        ///     Offset of our Switched Bank in CHR ROM
        /// </summary>
        private int _chrRomOffset;

        /// <summary>
        ///     PRG ROM Mirroring Enabled
        /// </summary>
        private readonly bool _prgRomMirroring;

        public enumNametableMirroring NametableMirroring { get; set; }

        public CNROM(byte[] prgRom, int prgRomBanks, byte[] chrRom, enumNametableMirroring nametableMirroring)
        {
            _chrRom = chrRom;
            _chrBankCount = Math.Max(1, _chrRom.Length / 0x2000);
            NametableMirroring = nametableMirroring;

            //Copy over all of PRG ROM (16KB or 32KB)
            Array.Copy(prgRom, 0, _prgRom, 0, prgRom.Length);

            //If it was only 16KB, go ahead and mirror the second 16KB
            if (prgRom.Length <= 0x4000)
                Array.Copy(prgRom, 0, _prgRom, 0x4000, prgRom.Length);
        }

        /// <summary>
        ///     Reads one byte from the specified bank, at the specified offset
        /// </summary>
        /// <param name="memoryType"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public byte ReadByte(int offset)
        {
            offset &= 0xFFFF;
            // CHR ROM
            if (offset < 0x2000)
                return _chrRom[_chrRomOffset + offset];

            //PPU Registers
            if (offset <= 0x3FFF)
                return ReadInterceptors.TryGetValue(offset, out currentReadInterceptor)
                    ? currentReadInterceptor(offset)
                    : (byte) 0x0;

            //Fixed PRG ROM
            if (offset <= 0xFFFF)
                return _prgRom[offset - 0x8000];

            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Maximum value of offset is 0xFFFF");
        }

        /// <summary>
        ///     Writes one byte to the specified bank, at the specified offset
        ///
        ///     CHR Bank Switching is handled at $8000->$FFFF
        /// </summary>
        /// <param name="memoryType"></param>
        /// <param name="offset"></param>
        /// <param name="data"></param>
        public void WriteByte(int offset, byte data)
        {
            offset &= 0xFFFF;
            //CHR ROM+RAM Writes
            if (offset < 0x2000)
            {
                // CNROM cartridges typically use CHR ROM; tolerate writes as no-op unless backing exists.
                if (_chrRomOffset + offset < _chrRom.Length)
                    _chrRom[_chrRomOffset + offset] = data;
                return;
            }

            //PPU Registers
            if (offset <= 0x3FFF || offset == 0x4014)
            {
                if (WriteInterceptors.TryGetValue(offset, out currentWriteInterceptor))
                    currentWriteInterceptor(offset, data);

                return;
            }

            //CHR Bank Select
            if (offset >= 0x8000 && offset <= 0xFFFF)
            {
                // Select bank within actual CHR ROM size (some ROMs expose fewer than 4 banks).
                int bank = data & 0x03;
                bank %= _chrBankCount;
                _chrRomOffset = bank * 0x2000;
                return;
            }

            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Maximum value of offset is 0xFFFF");
        }

        
    }
}
