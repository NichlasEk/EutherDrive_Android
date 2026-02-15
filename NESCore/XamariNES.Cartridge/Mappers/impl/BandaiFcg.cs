using System;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Common.Extensions;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 16 (Bandai FCG-1/FCG-2)
    ///
    ///     More Info: https://www.nesdev.org/wiki/Bandai_FCG
    /// </summary>
    public sealed class BandaiFcg : MapperBase, IMapper, IMapperIrqProvider, IMapperCpuTick, ISaveRamProvider
    {
        [NonSerialized]
        private readonly byte[] _prgRom;
        [NonSerialized]
        private readonly byte[] _chrRom;
        private readonly byte[] _prgRam;
        private readonly bool _useChrRam;
        private readonly int _prgBankCount16k;
        private readonly int _chrBankCount1k;

        private readonly byte[] _chrBanks = new byte[8];
        private byte _prgBank;
        private bool _ramEnabled;
        private RegisterRangeMode _registerRangeMode;

        private ushort _irqCounter;
        private bool _irqEnabled;

        private bool _saveRamDirty;
        public bool BatteryBacked { get; }

        public enumNametableMirroring NametableMirroring { get; set; }

        public bool IrqPending => _irqEnabled && _irqCounter == 0;

        public BandaiFcg(byte[] prgRom, byte[] chrRom, bool useChrRam, int prgRamSize, bool batteryBacked,
            enumNametableMirroring mirroring = enumNametableMirroring.Horizontal)
        {
            _prgRom = prgRom;
            _chrRom = chrRom;
            _useChrRam = useChrRam;
            _prgRam = new byte[Math.Max(1, prgRamSize)];
            _prgBankCount16k = Math.Max(1, _prgRom.Length / 0x4000);
            _chrBankCount1k = Math.Max(1, _chrRom.Length / 0x0400);
            BatteryBacked = batteryBacked;
            NametableMirroring = mirroring;

            for (int i = 0; i < _chrBanks.Length; i++)
                _chrBanks[i] = (byte)i;
        }

        public byte ReadByte(int offset)
        {
            if (offset < 0x2000)
            {
                int chrIndex = ResolveChrAddress(offset);
                return _chrRom[chrIndex];
            }

            if (offset <= 0x3FFF)
                return ReadInterceptors.TryGetValue(offset, out currentReadInterceptor)
                    ? currentReadInterceptor(offset)
                    : (byte)0x00;

            if (offset >= 0x6000 && offset <= 0x7FFF)
            {
                if (_ramEnabled && _prgRam.Length > 0)
                    return _prgRam[(offset - 0x6000) % _prgRam.Length];
                return 0x00;
            }

            if (offset >= 0x8000 && offset <= 0xBFFF)
            {
                int bank = _prgBank % _prgBankCount16k;
                return _prgRom[(bank * 0x4000) + (offset - 0x8000)];
            }

            if (offset >= 0xC000 && offset <= 0xFFFF)
            {
                int lastBank = _prgBankCount16k - 1;
                return _prgRom[(lastBank * 0x4000) + (offset - 0xC000)];
            }

            return 0x00;
        }

        public void WriteByte(int offset, byte data)
        {
            if (offset < 0x2000)
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
                if (_registerRangeMode == RegisterRangeMode.Unknown)
                    _registerRangeMode = RegisterRangeMode.Low;
                if (_registerRangeMode == RegisterRangeMode.Low)
                {
                    WriteRegister(offset, data);
                    return;
                }

                if (_registerRangeMode == RegisterRangeMode.High && _ramEnabled && _prgRam.Length > 0)
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

            if (offset >= 0x8000 && offset <= 0xFFFF)
            {
                _registerRangeMode = RegisterRangeMode.High;
                WriteRegister(offset, data);
                return;
            }
        }

        private void WriteRegister(int offset, byte data)
        {
            int reg = offset & 0x000F;
            if (reg <= 0x07)
            {
                _chrBanks[reg] = data;
                return;
            }

            switch (reg)
            {
                case 0x08:
                    _prgBank = (byte)(data & 0x0F);
                    break;
                case 0x09:
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
                    break;
                case 0x0A:
                    _irqEnabled = data.IsBitSet(0);
                    break;
                case 0x0B:
                    _irqCounter = (ushort)((_irqCounter & 0xFF00) | data);
                    break;
                case 0x0C:
                    _irqCounter = (ushort)((_irqCounter & 0x00FF) | (data << 8));
                    break;
                case 0x0D:
                    _ramEnabled = data.IsBitSet(5);
                    break;
            }
        }

        private int ResolveChrAddress(int offset)
        {
            int bankIndex = (offset >> 10) & 0x07;
            int bankOffset = offset & 0x03FF;
            int bank = _chrBanks[bankIndex] % _chrBankCount1k;
            return (bank * 0x0400) + bankOffset;
        }

        public void TickCpu(int cycles)
        {
            if (!_irqEnabled)
                return;
            for (int i = 0; i < cycles; i++)
            {
                if (_irqCounter > 0)
                    _irqCounter--;
            }
        }

        public bool IsSaveRamDirty => _saveRamDirty;

        public byte[] GetSaveRam() => _prgRam;

        public void ClearSaveRamDirty() => _saveRamDirty = false;

        private enum RegisterRangeMode
        {
            Unknown,
            Low,
            High
        }
    }
}
