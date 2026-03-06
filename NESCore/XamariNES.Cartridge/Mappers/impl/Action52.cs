using System;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Common.Extensions;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 228 (Action 52 / Cheetahmen II)
    /// </summary>
    public sealed class Action52 : MapperBase, IMapper, IMapperOpenBusRead
    {
        private enum PrgMode
        {
            Zero,
            One
        }

        [NonSerialized] private readonly byte[] _prgRom;
        [NonSerialized] private readonly byte[] _chrRom;
        private readonly int _prgBankMask;

        private int _selectedChip;
        private int _prgBank;
        private PrgMode _prgMode;
        private int _chrBank;

        public enumNametableMirroring NametableMirroring { get; set; }

        public Action52(byte[] prgRom, byte[] chrRom)
        {
            _prgRom = prgRom;
            _chrRom = chrRom;
            _prgBankMask = _prgRom.Length < (512 * 1024)
                ? (int)((_prgRom.Length - 1) >> 14)
                : 0xFF;
            NametableMirroring = enumNametableMirroring.Vertical;
        }

        public byte ReadByte(int offset)
        {
            return ReadByte(offset, 0x00);
        }

        public byte ReadByte(int offset, byte cpuOpenBus)
        {
            if (offset < 0x2000)
            {
                int chrAddr = ((_chrBank & 0xFF) << 13) | (offset & 0x1FFF);
                return _chrRom[chrAddr % _chrRom.Length];
            }

            if (offset <= 0x3FFF)
                return ReadInterceptors.TryGetValue(offset, out currentReadInterceptor)
                    ? currentReadInterceptor(offset)
                    : (byte)0x00;

            if (offset < 0x8000)
                return cpuOpenBus;

            int prgBank = offset <= 0xBFFF
                ? (_prgMode == PrgMode.Zero ? (_prgBank & ~1) : _prgBank)
                : (_prgMode == PrgMode.Zero ? (_prgBank | 1) : _prgBank);

            int fullPrgBank = (prgBank & _prgBankMask) | (_selectedChip << 5);
            int romAddr = (fullPrgBank << 14) | (offset & 0x3FFF);
            if ((uint)romAddr >= (uint)_prgRom.Length)
                return cpuOpenBus;
            return _prgRom[romAddr];
        }

        public void WriteByte(int offset, byte data)
        {
            if (offset < 0x2000)
            {
                int chrAddr = ((_chrBank & 0xFF) << 13) | (offset & 0x1FFF);
                _chrRom[chrAddr % _chrRom.Length] = data;
                return;
            }

            if (offset <= 0x3FFF || offset == 0x4014)
            {
                if (WriteInterceptors.TryGetValue(offset, out currentWriteInterceptor))
                    currentWriteInterceptor(offset, data);
                return;
            }

            if (offset < 0x8000)
                return;

            _chrBank = (data & 0x03) | ((offset & 0x000F) << 2);
            _prgMode = (offset & 0x0020) != 0 ? PrgMode.One : PrgMode.Zero;
            _prgBank = (offset >> 6) & 0x1F;

            if (_prgRom.Length > (512 * 1024))
            {
                int selectedChip = (offset >> 11) & 0x03;
                _selectedChip = selectedChip ^ (selectedChip >> 1);
            }

            NametableMirroring = (offset & 0x4000) != 0
                ? enumNametableMirroring.Horizontal
                : enumNametableMirroring.Vertical;
        }
    }
}
