using System;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Common.Extensions;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 30 (UNROM 512)
    /// </summary>
    public sealed class Unrom512 : MapperBase, IMapper, IMapperOpenBusRead, IPpuMemoryMapper, ISaveRamProvider
    {
        private enum HardwiredMirroring
        {
            Horizontal,
            Vertical,
            SingleScreen,
            FourScreen
        }

        [NonSerialized] private readonly byte[] _prgRom;
        [NonSerialized] private readonly byte[] _chrRam;

        private readonly int _prgBankCount16k;
        private readonly int _chrBankCount8k;
        private readonly bool _flashable;
        private readonly HardwiredMirroring _hardwiredMirroring;

        private int _prgBank;
        private int _chrBank;
        private bool _singleScreenSelect;

        private FlashWriteState _flashState = FlashWriteState.Prelude0;
        private byte _flashBank;
        private bool _saveDirty;

        public enumNametableMirroring NametableMirroring { get; set; }
        public bool BatteryBacked { get; }
        public bool IsSaveRamDirty => _saveDirty;

        public Unrom512(byte[] prgRom, byte[] chrRam, bool batteryBacked, bool fourScreenFlag,
            enumNametableMirroring headerMirroring)
        {
            _prgRom = prgRom;
            _chrRam = chrRam;
            _prgBankCount16k = Math.Max(1, _prgRom.Length / 0x4000);
            _chrBankCount8k = Math.Max(1, _chrRam.Length / 0x2000);
            _flashable = batteryBacked;
            BatteryBacked = batteryBacked;

            _hardwiredMirroring = fourScreenFlag
                ? (headerMirroring == enumNametableMirroring.Vertical ? HardwiredMirroring.FourScreen : HardwiredMirroring.SingleScreen)
                : (headerMirroring == enumNametableMirroring.Vertical ? HardwiredMirroring.Vertical : HardwiredMirroring.Horizontal);

            NametableMirroring = ResolveEffectiveMirroring();
        }

        public byte[] GetSaveRam() => _flashable ? _prgRom : _chrRam;

        public void ClearSaveRamDirty() => _saveDirty = false;

        public byte ReadByte(int offset)
        {
            return ReadByte(offset, 0x00);
        }

        public byte ReadByte(int offset, byte cpuOpenBus)
        {
            if (offset < 0x2000)
                return _chrRam[ResolveChrRamAddress(offset)];

            if (offset <= 0x3FFF)
                return ReadInterceptors.TryGetValue(offset, out currentReadInterceptor)
                    ? currentReadInterceptor(offset)
                    : (byte)0x00;

            if (offset < 0x8000)
                return cpuOpenBus;

            if (offset <= 0xBFFF)
                return _prgRom[ResolvePrgAddress(_prgBank, offset, 0x4000)];

            return _prgRom[ResolvePrgAddress(_prgBankCount16k - 1, offset, 0x4000)];
        }

        public void WriteByte(int offset, byte data)
        {
            if (offset < 0x2000)
            {
                _chrRam[ResolveChrRamAddress(offset)] = data;
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

            _singleScreenSelect = data.IsBitSet(7);
            _chrBank = (data >> 5) & 0x03;
            _prgBank = data & 0x1F;
            NametableMirroring = ResolveEffectiveMirroring();

            if (_flashable)
                TryFlashWrite(offset, data);
        }

        public byte ReadPpu(int address, byte[] vram)
        {
            if (address < 0x2000)
                return _chrRam[ResolveChrRamAddress(address)];

            if (_hardwiredMirroring == HardwiredMirroring.FourScreen)
                return _chrRam[ResolveFourScreenNametableAddress(address)];

            int vramIndex = MapVram(address);
            return vram[vramIndex];
        }

        public void WritePpu(int address, byte value, byte[] vram)
        {
            if (address < 0x2000)
            {
                _chrRam[ResolveChrRamAddress(address)] = value;
                return;
            }

            if (_hardwiredMirroring == HardwiredMirroring.FourScreen)
            {
                _chrRam[ResolveFourScreenNametableAddress(address)] = value;
                return;
            }

            int vramIndex = MapVram(address);
            vram[vramIndex] = value;
        }

        private int ResolveChrRamAddress(int address)
        {
            int bank = _chrBank % _chrBankCount8k;
            return (bank * 0x2000) + (address & 0x1FFF);
        }

        private int ResolveFourScreenNametableAddress(int address)
        {
            int chrRamLen = _chrRam.Length;
            int last8kBase = Math.Max(0, chrRamLen - 0x2000);
            return last8kBase + (address & 0x1FFF);
        }

        private static int ResolvePrgAddress(int bank, int address, int bankSize)
        {
            return (bank * bankSize) + (address & (bankSize - 1));
        }

        private enumNametableMirroring ResolveEffectiveMirroring()
        {
            switch (_hardwiredMirroring)
            {
                case HardwiredMirroring.Horizontal:
                    return enumNametableMirroring.Horizontal;
                case HardwiredMirroring.Vertical:
                    return enumNametableMirroring.Vertical;
                case HardwiredMirroring.SingleScreen:
                    return _singleScreenSelect ? enumNametableMirroring.SingleUpper : enumNametableMirroring.SingleLower;
                default:
                    return enumNametableMirroring.Vertical;
            }
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

        private enum FlashWriteState
        {
            Prelude0,
            Prelude1,
            Prelude2,
            Prelude3,
            Prelude4,
            ClearOrWrite,
            Clear0,
            Clear1,
            Clear2,
            Clear3,
            Clear4,
            ClearBank,
            ClearLast,
            WriteBank,
            WriteLast
        }

        private void TryFlashWrite(int address, byte value)
        {
            switch (_flashState)
            {
                case FlashWriteState.Prelude0:
                    _flashState = Match(address, value, 0xC000, 0x01) ? FlashWriteState.Prelude1 : FlashWriteState.Prelude0;
                    break;
                case FlashWriteState.Prelude1:
                    _flashState = Match(address, value, 0x9555, 0xAA) ? FlashWriteState.Prelude2 : FlashWriteState.Prelude0;
                    break;
                case FlashWriteState.Prelude2:
                    _flashState = Match(address, value, 0xC000, 0x00) ? FlashWriteState.Prelude3 : FlashWriteState.Prelude0;
                    break;
                case FlashWriteState.Prelude3:
                    _flashState = Match(address, value, 0xAAAA, 0x55) ? FlashWriteState.Prelude4 : FlashWriteState.Prelude0;
                    break;
                case FlashWriteState.Prelude4:
                    _flashState = Match(address, value, 0xC000, 0x01) ? FlashWriteState.ClearOrWrite : FlashWriteState.Prelude0;
                    break;
                case FlashWriteState.ClearOrWrite:
                    if (Match(address, value, 0x9555, 0x80)) _flashState = FlashWriteState.Clear0;
                    else if (Match(address, value, 0x9555, 0xA0)) _flashState = FlashWriteState.WriteBank;
                    else _flashState = FlashWriteState.Prelude0;
                    break;
                case FlashWriteState.Clear0:
                    _flashState = Match(address, value, 0xC000, 0x01) ? FlashWriteState.Clear1 : FlashWriteState.Prelude0;
                    break;
                case FlashWriteState.Clear1:
                    _flashState = Match(address, value, 0x9555, 0xAA) ? FlashWriteState.Clear2 : FlashWriteState.Prelude0;
                    break;
                case FlashWriteState.Clear2:
                    _flashState = Match(address, value, 0xC000, 0x00) ? FlashWriteState.Clear3 : FlashWriteState.Prelude0;
                    break;
                case FlashWriteState.Clear3:
                    _flashState = Match(address, value, 0xAAAA, 0x55) ? FlashWriteState.Clear4 : FlashWriteState.Prelude0;
                    break;
                case FlashWriteState.Clear4:
                    _flashState = FlashWriteState.ClearBank;
                    goto case FlashWriteState.ClearBank;
                case FlashWriteState.ClearBank:
                    if (address == 0xC000)
                    {
                        _flashBank = value;
                        _flashState = FlashWriteState.ClearLast;
                    }
                    else
                    {
                        _flashState = FlashWriteState.Prelude0;
                    }
                    break;
                case FlashWriteState.ClearLast:
                    if (value == 0x30)
                    {
                        int baseAddr = ((_flashBank << 14) | (address & 0x3000)) & (_prgRom.Length - 1);
                        int endAddr = Math.Min(_prgRom.Length, baseAddr + 0x1000);
                        Array.Clear(_prgRom, baseAddr, endAddr - baseAddr);
                        _saveDirty = true;
                    }
                    _flashState = FlashWriteState.Prelude0;
                    break;
                case FlashWriteState.WriteBank:
                    if (address == 0xC000)
                    {
                        _flashBank = value;
                        _flashState = FlashWriteState.WriteLast;
                    }
                    else
                    {
                        _flashState = FlashWriteState.Prelude0;
                    }
                    break;
                case FlashWriteState.WriteLast:
                    {
                        int romAddr = ((_flashBank << 14) | (address & 0x3FFF)) & (_prgRom.Length - 1);
                        _prgRom[romAddr] = value;
                        _saveDirty = true;
                        _flashState = FlashWriteState.Prelude0;
                        break;
                    }
            }
        }

        private static bool Match(int address, byte value, int expectedAddress, byte expectedValue)
        {
            return address == expectedAddress && value == expectedValue;
        }
    }
}
