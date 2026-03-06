using System;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Common.Extensions;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 85 (Konami VRC7)
    /// </summary>
    public sealed class VRC7 : MapperBase, IMapper, IMapperIrqProvider, IMapperCpuTick, IMapperOpenBusRead,
        IExpansionAudioProvider, ISaveRamProvider
    {
        private enum Variant
        {
            Vrc7a,
            Vrc7b,
            Unknown
        }

        [NonSerialized] private readonly byte[] _prgRom;
        [NonSerialized] private readonly byte[] _chrRom;
        private readonly byte[] _prgRam;
        private readonly bool _useChrRam;
        private readonly int _prgBankCount8k;
        private readonly int _chrBankCount1k;

        private readonly Variant _variant;
        private readonly KonamiIrqCounter _irq = new KonamiIrqCounter();

        private byte _prgBank0;
        private byte _prgBank1;
        private byte _prgBank2;
        private readonly byte[] _chrBanks = new byte[8];
        private bool _ramEnabled;

        private bool _audioEnabled;
        private byte _audioRegister;
        private readonly byte[] _audioRegs = new byte[0x40];

        private bool _saveRamDirty;

        public enumNametableMirroring NametableMirroring { get; set; }
        public bool IrqPending => _irq.InterruptFlag;
        public bool BatteryBacked { get; }
        public bool IsSaveRamDirty => _saveRamDirty;

        public VRC7(byte[] prgRom, byte[] chrRom, bool useChrRam, int prgRamSize, bool batteryBacked,
            int subMapper, enumNametableMirroring mirroring = enumNametableMirroring.Vertical)
        {
            _prgRom = prgRom;
            _chrRom = chrRom;
            _useChrRam = useChrRam;
            _prgRam = new byte[Math.Max(1, prgRamSize)];
            _prgBankCount8k = Math.Max(1, _prgRom.Length / 0x2000);
            _chrBankCount1k = Math.Max(1, _chrRom.Length / 0x0400);
            _variant = subMapper == 1 ? Variant.Vrc7b : subMapper == 2 ? Variant.Vrc7a : Variant.Unknown;
            NametableMirroring = mirroring;
            BatteryBacked = batteryBacked;
        }

        public byte[] GetSaveRam() => _prgRam;

        public void ClearSaveRamDirty() => _saveRamDirty = false;

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
                if (_ramEnabled)
                    return _prgRam[(offset - 0x6000) % _prgRam.Length];
                return cpuOpenBus;
            }

            if (offset >= 0x8000 && offset <= 0x9FFF)
                return ReadPrg8k(_prgBank0, offset);
            if (offset >= 0xA000 && offset <= 0xBFFF)
                return ReadPrg8k(_prgBank1, offset);
            if (offset >= 0xC000 && offset <= 0xDFFF)
                return ReadPrg8k(_prgBank2, offset);
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
                if (_ramEnabled)
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

            if (offset < 0x8000)
                return;

            switch (offset)
            {
                case 0x8000:
                    _prgBank0 = (byte)(data & 0x3F);
                    return;
                case 0x8010:
                    if (_variant == Variant.Vrc7a || _variant == Variant.Unknown)
                    {
                        _prgBank1 = (byte)(data & 0x3F);
                        return;
                    }
                    break;
                case 0x8008:
                    if (_variant == Variant.Vrc7b || _variant == Variant.Unknown)
                    {
                        _prgBank1 = (byte)(data & 0x3F);
                        return;
                    }
                    break;
                case 0x9000:
                    _prgBank2 = (byte)(data & 0x3F);
                    return;
                case 0x9010:
                    if (_variant == Variant.Vrc7a || _variant == Variant.Unknown)
                    {
                        _audioRegister = data;
                        return;
                    }
                    break;
                case 0x9030:
                    if (_variant == Variant.Vrc7a || _variant == Variant.Unknown)
                    {
                        if (_audioEnabled)
                            _audioRegs[_audioRegister & 0x3F] = data;
                        return;
                    }
                    break;
                case 0xE000:
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
                    _ramEnabled = data.IsBitSet(7);
                    _audioEnabled = !data.IsBitSet(6);
                    if (!_audioEnabled)
                        Array.Clear(_audioRegs, 0, _audioRegs.Length);
                    return;
                case 0xE010:
                    if (_variant == Variant.Vrc7a || _variant == Variant.Unknown)
                    {
                        _irq.SetReloadValue(data);
                        return;
                    }
                    break;
                case 0xE008:
                    if (_variant == Variant.Vrc7b || _variant == Variant.Unknown)
                    {
                        _irq.SetReloadValue(data);
                        return;
                    }
                    break;
                case 0xF000:
                    _irq.SetControl(data);
                    return;
                case 0xF010:
                    if (_variant == Variant.Vrc7a || _variant == Variant.Unknown)
                    {
                        _irq.Acknowledge();
                        return;
                    }
                    break;
                case 0xF008:
                    if (_variant == Variant.Vrc7b || _variant == Variant.Unknown)
                    {
                        _irq.Acknowledge();
                        return;
                    }
                    break;
            }

            if (offset >= 0xA000 && offset <= 0xD010)
            {
                int addressMask = _variant == Variant.Vrc7a ? 0x0010 : _variant == Variant.Vrc7b ? 0x0008 : 0x0018;
                int chrBankIndex = 2 * ((offset - 0xA000) / 0x1000) + (((offset & addressMask) != 0) ? 1 : 0);
                if ((uint)chrBankIndex < 8)
                    _chrBanks[chrBankIndex] = data;
            }
        }

        public void TickCpu(int cycles)
        {
            for (int i = 0; i < cycles; i++)
                _irq.TickCpu();
        }

        public double MixAudio(double apuSample)
        {
            // FM core not yet emulated; keep gameplay correctness and IRQ behavior.
            return apuSample;
        }

        private byte ReadPrg8k(int bank, int offset)
        {
            int mapped = PositiveMod(bank, _prgBankCount8k);
            return _prgRom[(mapped * 0x2000) + (offset & 0x1FFF)];
        }

        private static int PositiveMod(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }
    }
}
