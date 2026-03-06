using System;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Common.Extensions;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 9/10 (MMC2/MMC4)
    /// </summary>
    public sealed class MMC2 : MapperBase, IMapper, IPpuMemoryMapper
    {
        private enum Variant
        {
            Mmc2,
            Mmc4
        }

        private enum ChrLatch
        {
            FD,
            FE
        }

        [NonSerialized] private readonly byte[] _prgRom;
        [NonSerialized] private readonly byte[] _chrRom;
        private readonly byte[] _prgRam;
        private readonly bool _hasPrgRam;
        private readonly Variant _variant;

        private readonly int _prgBankCount8k;
        private readonly int _prgBankCount16k;
        private readonly int _chrBankCount4k;

        private int _prgBank;
        private int _chr0FdBank;
        private int _chr0FeBank;
        private ChrLatch _chr0Latch = ChrLatch.FD;
        private int _chr1FdBank;
        private int _chr1FeBank;
        private ChrLatch _chr1Latch = ChrLatch.FD;

        public enumNametableMirroring NametableMirroring { get; set; }

        public MMC2(byte[] prgRom, byte[] chrRom, int prgRamSize, bool mmc4, enumNametableMirroring mirroring)
        {
            _prgRom = prgRom;
            _chrRom = chrRom;
            _prgRam = new byte[Math.Max(1, prgRamSize)];
            _hasPrgRam = prgRamSize > 0;
            _variant = mmc4 ? Variant.Mmc4 : Variant.Mmc2;
            _prgBankCount8k = Math.Max(1, _prgRom.Length / 0x2000);
            _prgBankCount16k = Math.Max(1, _prgRom.Length / 0x4000);
            _chrBankCount4k = Math.Max(1, _chrRom.Length / 0x1000);
            NametableMirroring = mirroring;
        }

        public byte ReadByte(int offset)
        {
            if (offset < 0x2000)
            {
                int bank = offset < 0x1000
                    ? (_chr0Latch == ChrLatch.FD ? _chr0FdBank : _chr0FeBank)
                    : (_chr1Latch == ChrLatch.FD ? _chr1FdBank : _chr1FeBank);
                int baseAddr = (bank % _chrBankCount4k) * 0x1000;
                return _chrRom[baseAddr + (offset & 0x0FFF)];
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

            if (_variant == Variant.Mmc2)
            {
                if (offset >= 0x8000 && offset <= 0x9FFF)
                    return ReadPrg8k(_prgBank, offset);
                if (offset >= 0xA000 && offset <= 0xBFFF)
                    return ReadPrg8k(_prgBankCount8k - 3, offset);
                if (offset >= 0xC000 && offset <= 0xDFFF)
                    return ReadPrg8k(_prgBankCount8k - 2, offset);
                if (offset >= 0xE000)
                    return ReadPrg8k(_prgBankCount8k - 1, offset);
            }
            else
            {
                if (offset >= 0x8000 && offset <= 0xBFFF)
                    return ReadPrg16k(_prgBank, offset);
                if (offset >= 0xC000)
                    return ReadPrg16k(_prgBankCount16k - 1, offset);
            }

            return 0x00;
        }

        public void WriteByte(int offset, byte data)
        {
            if (offset < 0x2000)
                return;

            if (offset <= 0x3FFF || offset == 0x4014)
            {
                if (WriteInterceptors.TryGetValue(offset, out currentWriteInterceptor))
                    currentWriteInterceptor(offset, data);
                return;
            }

            if (offset >= 0x6000 && offset <= 0x7FFF)
            {
                if (_hasPrgRam)
                    _prgRam[(offset - 0x6000) % _prgRam.Length] = data;
                return;
            }

            if (offset >= 0xA000 && offset <= 0xAFFF)
            {
                _prgBank = data & 0x0F;
                return;
            }
            if (offset >= 0xB000 && offset <= 0xBFFF)
            {
                _chr0FdBank = data & 0x1F;
                return;
            }
            if (offset >= 0xC000 && offset <= 0xCFFF)
            {
                _chr0FeBank = data & 0x1F;
                return;
            }
            if (offset >= 0xD000 && offset <= 0xDFFF)
            {
                _chr1FdBank = data & 0x1F;
                return;
            }
            if (offset >= 0xE000 && offset <= 0xEFFF)
            {
                _chr1FeBank = data & 0x1F;
                return;
            }
            if (offset >= 0xF000)
            {
                NametableMirroring = data.IsBitSet(0)
                    ? enumNametableMirroring.Horizontal
                    : enumNametableMirroring.Vertical;
            }
        }

        public byte ReadPpu(int address, byte[] vram)
        {
            byte value;
            if (address < 0x2000)
            {
                value = ReadByte(address);
            }
            else if (address <= 0x3EFF)
            {
                int vramIndex = MapVram(address);
                value = vram[vramIndex];
            }
            else
            {
                value = 0;
            }

            switch (_variant)
            {
                case Variant.Mmc2:
                    if (address == 0x0FD8) _chr0Latch = ChrLatch.FD;
                    else if (address == 0x0FE8) _chr0Latch = ChrLatch.FE;
                    break;
                case Variant.Mmc4:
                    if (address >= 0x0FD8 && address <= 0x0FDF) _chr0Latch = ChrLatch.FD;
                    else if (address >= 0x0FE8 && address <= 0x0FEF) _chr0Latch = ChrLatch.FE;
                    break;
            }

            if (address >= 0x1FD8 && address <= 0x1FDF)
                _chr1Latch = ChrLatch.FD;
            else if (address >= 0x1FE8 && address <= 0x1FEF)
                _chr1Latch = ChrLatch.FE;

            return value;
        }

        public void WritePpu(int address, byte value, byte[] vram)
        {
            if (address >= 0x2000 && address <= 0x3EFF)
            {
                int vramIndex = MapVram(address);
                vram[vramIndex] = value;
            }
        }

        private byte ReadPrg8k(int bank, int offset)
        {
            int mappedBank = PositiveMod(bank, _prgBankCount8k);
            return _prgRom[(mappedBank * 0x2000) + (offset & 0x1FFF)];
        }

        private byte ReadPrg16k(int bank, int offset)
        {
            int mappedBank = PositiveMod(bank, _prgBankCount16k);
            return _prgRom[(mappedBank * 0x4000) + (offset & 0x3FFF)];
        }

        private int MapVram(int address)
        {
            int index = (address - 0x2000) & 0x0FFF;
            switch (NametableMirroring)
            {
                case enumNametableMirroring.Vertical:
                    if (index >= 0x800) index -= 0x800;
                    break;
                case enumNametableMirroring.Horizontal:
                    index = index >= 0x800 ? ((index - 0x800) % 0x400) + 0x400 : (index % 0x400);
                    break;
                case enumNametableMirroring.SingleLower:
                    index %= 0x400;
                    break;
                case enumNametableMirroring.SingleUpper:
                    index = (index % 0x400) + 0x400;
                    break;
            }
            return index;
        }

        private static int PositiveMod(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }
    }
}
