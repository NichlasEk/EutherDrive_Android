using System;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Common.Extensions;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 4 (MMC3)
    ///
    ///     More Info: https://www.nesdev.org/wiki/MMC3
    /// </summary>
    public class MMC3 : MapperBase, IMapper, IMapperIrqProvider, IPpuA12Observer, IMapperScanlineCounter, ISaveRamProvider
    {
        private readonly byte[] _prgRom;
        private readonly byte[] _chrRom;
        private readonly byte[] _prgRam;
        private bool _saveRamDirty;
        public bool BatteryBacked { get; }
        private readonly bool _useChrRam;
        private readonly int _prgBankCount8k;
        private readonly int _chrBankCount1k;

        private byte _bankSelect;
        private readonly byte[] _bankRegs = new byte[8];
        private bool _prgMode;
        private bool _chrMode;

        private byte _irqLatch;
        private byte _irqCounter;
        private bool _irqReload;
        private bool _irqEnabled;
        private bool _irqPending;
        private bool _lastA12;
        private long _lastA12LowCycle = -100000;
        private readonly bool _useScanlineClock = true;

        public enumNametableMirroring NametableMirroring { get; set; }

        public bool IrqPending => _irqPending;

        public MMC3(byte[] prgRom, byte[] chrRom, bool useChrRam, int prgRamSize, bool batteryBacked,
            enumNametableMirroring mirroring = enumNametableMirroring.Horizontal)
        {
            _prgRom = prgRom;
            _chrRom = chrRom;
            _useChrRam = useChrRam;
            _prgRam = new byte[Math.Max(1, prgRamSize)];
            _prgBankCount8k = Math.Max(1, _prgRom.Length / 0x2000);
            _chrBankCount1k = Math.Max(1, _chrRom.Length / 0x0400);
            NametableMirroring = mirroring;
            BatteryBacked = batteryBacked;
        }

        public byte ReadByte(int offset)
        {
            if (offset <= 0x1FFF)
            {
                int chrIndex = ResolveChrAddress(offset);
                return _chrRom[chrIndex];
            }

            if (offset <= 0x3FFF)
                return ReadInterceptors.TryGetValue(offset, out currentReadInterceptor) ? currentReadInterceptor(offset) : (byte)0x00;

            if (offset >= 0x6000 && offset <= 0x7FFF)
                return _prgRam[offset - 0x6000];

            if (offset >= 0x8000 && offset <= 0xFFFF)
            {
                int prgIndex = ResolvePrgAddress(offset);
                return _prgRom[prgIndex];
            }

            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Maximum value of offset is 0xFFFF");
        }

        public void WriteByte(int offset, byte data)
        {
            if (offset <= 0x1FFF)
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

            if (offset >= 0x6000 && offset <= 0x7FFF)
            {
                int idx = (offset - 0x6000) % _prgRam.Length;
                if (_prgRam[idx] != data)
                {
                    _prgRam[idx] = data;
                    _saveRamDirty = true;
                }
                return;
            }

            if (offset >= 0x8000 && offset <= 0xFFFF)
            {
                WriteRegister(offset, data);
                return;
            }

            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Maximum value of offset is 0xFFFF");
        }

        private void WriteRegister(int offset, byte data)
        {
            switch (offset & 0xE001)
            {
                case 0x8000:
                    _bankSelect = data;
                    _prgMode = data.IsBitSet(6);
                    _chrMode = data.IsBitSet(7);
                    break;
                case 0x8001:
                {
                    int reg = _bankSelect & 0x07;
                    _bankRegs[reg] = data;
                    break;
                }
                case 0xA000:
                    NametableMirroring = data.IsBitSet(0)
                        ? enumNametableMirroring.Horizontal
                        : enumNametableMirroring.Vertical;
                    break;
                case 0xA001:
                    // PRG RAM protect - ignored for now
                    break;
                case 0xC000:
                    _irqLatch = data;
                    break;
                case 0xC001:
                    _irqReload = true;
                    break;
                case 0xE000:
                    _irqEnabled = false;
                    _irqPending = false;
                    break;
                case 0xE001:
                    _irqEnabled = true;
                    break;
            }
        }

        private int ResolvePrgAddress(int offset)
        {
            int bankIndex;
            int prgOffset = offset - 0x8000;
            int bankSlot = prgOffset / 0x2000;
            int bankOffset = prgOffset % 0x2000;

            int lastBank = _prgBankCount8k - 1;
            int secondLastBank = _prgBankCount8k - 2;
            int bank6 = _bankRegs[6] % _prgBankCount8k;
            int bank7 = _bankRegs[7] % _prgBankCount8k;

            if (!_prgMode)
            {
                if (bankSlot == 0) bankIndex = bank6;
                else if (bankSlot == 1) bankIndex = bank7;
                else if (bankSlot == 2) bankIndex = secondLastBank;
                else bankIndex = lastBank;
            }
            else
            {
                if (bankSlot == 0) bankIndex = secondLastBank;
                else if (bankSlot == 1) bankIndex = bank7;
                else if (bankSlot == 2) bankIndex = bank6;
                else bankIndex = lastBank;
            }

            return (bankIndex * 0x2000) + bankOffset;
        }

        private int ResolveChrAddress(int offset)
        {
            int bankIndex;
            if (!_chrMode)
            {
                if (offset < 0x0800)
                    bankIndex = (_bankRegs[0] & 0xFE) + (offset / 0x0400);
                else if (offset < 0x1000)
                    bankIndex = (_bankRegs[1] & 0xFE) + ((offset - 0x0800) / 0x0400);
                else if (offset < 0x1400)
                    bankIndex = _bankRegs[2];
                else if (offset < 0x1800)
                    bankIndex = _bankRegs[3];
                else if (offset < 0x1C00)
                    bankIndex = _bankRegs[4];
                else
                    bankIndex = _bankRegs[5];
            }
            else
            {
                if (offset < 0x0400)
                    bankIndex = _bankRegs[2];
                else if (offset < 0x0800)
                    bankIndex = _bankRegs[3];
                else if (offset < 0x0C00)
                    bankIndex = _bankRegs[4];
                else if (offset < 0x1000)
                    bankIndex = _bankRegs[5];
                else if (offset < 0x1800)
                    bankIndex = (_bankRegs[0] & 0xFE) + ((offset - 0x1000) / 0x0400);
                else
                    bankIndex = (_bankRegs[1] & 0xFE) + ((offset - 0x1800) / 0x0400);
            }

            bankIndex %= _chrBankCount1k;
            int bankOffset = offset & 0x03FF;
            return bankIndex * 0x0400 + bankOffset;
        }

        public void NotifyPpuA12(int ppuAddress, long ppuCycle)
        {
            if (_useScanlineClock)
                return;
            UpdateA12(ppuAddress, ppuCycle);
        }

        private void UpdateA12(int ppuAddress, long ppuCycle)
        {
            bool a12 = (ppuAddress & 0x1000) != 0;
            if (!a12)
                _lastA12LowCycle = ppuCycle;

            if (!_lastA12 && a12)
            {
                if (ppuCycle - _lastA12LowCycle >= 12)
                {
                    if (_irqCounter == 0 || _irqReload)
                    {
                        _irqCounter = _irqLatch;
                        _irqReload = false;
                    }
                    else
                    {
                        _irqCounter--;
                    }

                    if (_irqCounter == 0 && _irqEnabled)
                        _irqPending = true;
                }
            }

            _lastA12 = a12;
        }

        public void ClockScanline()
        {
            if (_irqCounter == 0 || _irqReload)
            {
                _irqCounter = _irqLatch;
                _irqReload = false;
            }
            else
            {
                _irqCounter--;
            }

            if (_irqCounter == 0 && _irqEnabled)
                _irqPending = true;
        }

        public bool IsSaveRamDirty => _saveRamDirty;

        public byte[] GetSaveRam() => _prgRam;

        public void ClearSaveRamDirty()
        {
            _saveRamDirty = false;
        }
    }
}
