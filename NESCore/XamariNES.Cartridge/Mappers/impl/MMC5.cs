using System;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Common.Extensions;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 5 (MMC5)
    ///     More Info: https://www.nesdev.org/wiki/MMC5
    /// </summary>
    public class MMC5 : MapperBase, IMapper, IMapperIrqProvider, ISaveRamProvider, IMapperCpuTick,
        IPpuCtrlObserver, IPpuMaskObserver, IPpuDataObserver, IPpuMemoryMapper, IPpuMemoryMapperEx,
        IPpuScanlineObserver, IExpansionAudioProvider
    {
        private const int CpuCyclesPerQuarterFrame = 7457;

        [NonSerialized]
        private readonly byte[] _prgRom;
        [NonSerialized]
        private readonly byte[] _chrRom;
        private readonly byte[] _prgRam;
        private readonly bool _useChrRam;
        private readonly int _prgRomSize;
        private readonly int _chrRomSize;

        private readonly byte[] _extendedRam = new byte[1024];
        private ExtendedRamMode _extendedRamMode = ExtendedRamMode.ReadOnly;

        private PrgBankingMode _prgBankingMode = PrgBankingMode.Mode3;
        private readonly byte[] _prgBankRegisters = new byte[5];
        private readonly ChrMapper _chrMapper = new ChrMapper();
        private readonly NametableMapping[] _nametableMappings = new NametableMapping[4];

        private byte _fillModeTileData;
        private byte _fillModeAttributes;
        private readonly VerticalSplit _verticalSplit = new VerticalSplit();
        private readonly ScanlineCounter _scanlineCounter = new ScanlineCounter();
        private readonly bool _useExternalScanlineCounter = true;
        private readonly ExtendedAttributesState _extendedAttributesState = new ExtendedAttributesState();
        private MultiplierUnit _multiplier = new MultiplierUnit();

        private readonly PulseChannel _pulse1 = new PulseChannel();
        private readonly PulseChannel _pulse2 = new PulseChannel();
        private readonly PcmChannel _pcm = new PcmChannel();
        private int _frameCounterTicks;

        private bool _ramWritesEnabled1;
        private bool _ramWritesEnabled2;
        private bool _renderingEnabled;
        private byte _ppuOpenBus;
        private byte _cpuOpenBus;

        private bool _saveRamDirty;
        public bool BatteryBacked { get; }

        public enumNametableMirroring NametableMirroring { get; set; } = enumNametableMirroring.Horizontal;

        public bool IrqPending => _scanlineCounter.InterruptFlag() || _pcm.IrqPending;

        public MMC5(byte[] prgRom, byte[] chrRom, bool useChrRam, int prgRamSize, bool batteryBacked,
            enumNametableMirroring mirroring = enumNametableMirroring.Horizontal)
        {
            _prgRom = prgRom;
            _chrRom = chrRom;
            _useChrRam = useChrRam;
            _prgRomSize = _prgRom.Length;
            _chrRomSize = _chrRom.Length;
            _prgRam = new byte[Math.Max(1, prgRamSize)];
            BatteryBacked = batteryBacked;
            NametableMirroring = mirroring;

            for (int i = 0; i < _prgBankRegisters.Length; i++)
                _prgBankRegisters[i] = 0xFF;

            for (int i = 0; i < _nametableMappings.Length; i++)
                _nametableMappings[i] = NametableMapping.VramPage0;
        }

        public void OnPpuCtrlWrite(byte value) => _chrMapper.ProcessPpuCtrlUpdate(value);

        public void OnPpuMaskWrite(byte value) => _renderingEnabled = (value & 0x18) != 0;

        public void OnPpuDataAccess() => _chrMapper.NextAccessFromPpuData = true;

        public byte ReadByte(int offset)
        {
            if (offset == 0xFFFA || offset == 0xFFFB)
                _scanlineCounter.NmiVectorFetched();

            byte value;
            if (offset <= 0x1FFF)
            {
                value = ReadChr(offset);
            }
            else if (offset <= 0x3FFF)
            {
                value = ReadInterceptors.TryGetValue(offset, out currentReadInterceptor) ? currentReadInterceptor(offset) : (byte)0x00;
            }
            else if (offset <= 0x4FFF)
            {
                value = _cpuOpenBus;
            }
            else if (offset <= 0x5BFF)
            {
                value = ReadInternalRegister(offset, _cpuOpenBus);
            }
            else if (offset <= 0x5FFF)
            {
                value = ReadExtendedRam(offset);
            }
            else
            {
                value = ReadPrg(offset);
            }

            _cpuOpenBus = value;
            return value;
        }

        public void WriteByte(int offset, byte data)
        {
            _cpuOpenBus = data;

            if (offset <= 0x1FFF)
            {
                if (_useChrRam)
                {
                    int idx = offset % _chrRom.Length;
                    _chrRom[idx] = data;
                }
                return;
            }

            if (offset <= 0x3FFF || offset == 0x4014)
            {
                if (WriteInterceptors.TryGetValue(offset, out currentWriteInterceptor))
                    currentWriteInterceptor(offset, data);
                return;
            }

            if (offset <= 0x4FFF)
                return;

            if (offset <= 0x5BFF)
            {
                WriteInternalRegister(offset, data);
                return;
            }

            if (offset <= 0x5FFF)
            {
                if (_extendedRamMode != ExtendedRamMode.ReadOnly)
                    _extendedRam[offset - 0x5C00] = data;
                return;
            }

            if (PrgRamWritesEnabled())
                WritePrg(offset, data);
        }

        public byte ReadPpu(int address, byte[] vram)
        {
            if (!_useExternalScanlineCounter)
                _scanlineCounter.PreFetch();

            byte value;
            if (address < 0x2000)
            {
                TileType tileType = _scanlineCounter.CurrentTileType();
                if (_renderingEnabled && tileType == TileType.Background &&
                    _extendedRamMode == ExtendedRamMode.NametableExtendedAttributes)
                {
                    value = _extendedAttributesState.GetPatternTableByte(address, _extendedRam, _chrRom);
                }
                else if (_renderingEnabled && tileType == TileType.Background &&
                         _verticalSplit.InsideSplit(_scanlineCounter))
                {
                    int fineYScroll = _verticalSplit.YScroll & 0x07;
                    int patternTableAddr = (address & 0xFFF8) | ((address + fineYScroll) & 0x07);
                    value = ReadChrFromBank(BankSizeKb.Four, _verticalSplit.ChrBank, patternTableAddr);
                }
                else
                {
                    int chrAddr = _chrMapper.MapChrAddress(address, tileType);
                    value = ReadChr(chrAddr);
                }

                _scanlineCounter.IncrementTileBytesFetched();
            }
            else if (address <= 0x3EFF)
            {
                int relative = address & 0x0FFF;
                int nametableAddr = 0x2000 | relative;
                _scanlineCounter.NametableAddressFetched(nametableAddr);

                TileType tileType = _scanlineCounter.CurrentTileType();
                if (tileType == TileType.Background &&
                    _extendedRamMode == ExtendedRamMode.NametableExtendedAttributes &&
                    (address & 0x03FF) >= 0x03C0)
                {
                    _extendedAttributesState.LastNametableAddr = address;
                    value = _extendedAttributesState.GetAttributeByte(_extendedRam);
                }
                else
                {
                    _extendedAttributesState.LastNametableAddr = address;

                    if (_verticalSplit.InsideSplit(_scanlineCounter) &&
                        (_extendedRamMode == ExtendedRamMode.Nametable ||
                         _extendedRamMode == ExtendedRamMode.NametableExtendedAttributes))
                    {
                        int scanline = _scanlineCounter.Scanline;
                        int yScroll = _verticalSplit.YScroll;
                        int tileXIndex = _scanlineCounter.CurrentTileIndex() & 0x1F;
                        int tileYIndex = ((scanline + yScroll) >> 3) % 30;

                        int extendedRamAddr;
                        if ((relative & 0x03FF) < 0x03C0)
                            extendedRamAddr = (tileYIndex << 5) | tileXIndex;
                        else
                            extendedRamAddr = 0x03C0 + (((tileYIndex >> 2) << 3) | (tileXIndex >> 2));

                        value = _extendedRam[extendedRamAddr & 0x03FF];
                    }
                    else
                    {
                        NametableMapping mapping = _nametableMappings[(relative >> 10) & 0x03];
                        switch (mapping)
                        {
                            case NametableMapping.VramPage0:
                                value = vram[relative & 0x03FF];
                                break;
                            case NametableMapping.VramPage1:
                                value = vram[0x0400 | (relative & 0x03FF)];
                                break;
                            case NametableMapping.ExtendedRam:
                                if (_extendedRamMode == ExtendedRamMode.Nametable ||
                                    _extendedRamMode == ExtendedRamMode.NametableExtendedAttributes)
                                    value = _extendedRam[relative & 0x03FF];
                                else
                                    value = _ppuOpenBus;
                                break;
                            case NametableMapping.FillMode:
                                value = (relative & 0x03FF) < 0x03C0 ? _fillModeTileData : _fillModeAttributes;
                                break;
                            default:
                                value = _ppuOpenBus;
                                break;
                        }
                    }
                }
            }
            else
            {
                value = _ppuOpenBus;
            }

            _ppuOpenBus = value;
            return value;
        }

        public byte ReadPpuRender(int address, byte[] vram, bool sprite)
        {
            if (address >= 0x2000)
                return _ppuOpenBus;

            TileType tileType = sprite ? TileType.Sprite : TileType.Background;
            int chrAddr;
            if (_renderingEnabled && tileType == TileType.Background &&
                _extendedRamMode == ExtendedRamMode.NametableExtendedAttributes)
            {
                chrAddr = _extendedAttributesState.GetPatternTableAddress(address, _extendedRam);
            }
            else if (_renderingEnabled && tileType == TileType.Background &&
                     _verticalSplit.InsideSplit(_scanlineCounter))
            {
                int fineYScroll = _verticalSplit.YScroll & 0x07;
                int patternTableAddr = (address & 0xFFF8) | ((address + fineYScroll) & 0x07);
                chrAddr = BankSizeKbHelper.ToBytes(BankSizeKb.Four) * _verticalSplit.ChrBank + (patternTableAddr & 0x0FFF);
            }
            else
            {
                chrAddr = _chrMapper.MapChrAddress(address, tileType);
            }

            byte value = ReadChr(chrAddr);
            _ppuOpenBus = value;
            return value;
        }

        public void WritePpu(int address, byte value, byte[] vram)
        {
            _ppuOpenBus = value;

            if (address < 0x2000)
            {
                if (_useChrRam)
                {
                    int idx = address % _chrRom.Length;
                    _chrRom[idx] = value;
                }
                return;
            }

            if (address <= 0x3EFF)
            {
                int relative = address & 0x0FFF;
                NametableMapping mapping = _nametableMappings[(relative >> 10) & 0x03];
                switch (mapping)
                {
                    case NametableMapping.VramPage0:
                        vram[relative & 0x03FF] = value;
                        break;
                    case NametableMapping.VramPage1:
                        vram[0x0400 | (relative & 0x03FF)] = value;
                        break;
                    case NametableMapping.ExtendedRam:
                        _extendedRam[relative & 0x03FF] = value;
                        break;
                    case NametableMapping.FillMode:
                        break;
                }
            }
        }

        public void TickCpu(int cycles)
        {
            for (int i = 0; i < cycles; i++)
            {
                if (!_useExternalScanlineCounter)
                    _scanlineCounter.TickCpu();
                _pulse1.TickCpu();
                _pulse2.TickCpu();

                _frameCounterTicks++;
                if (_frameCounterTicks >= CpuCyclesPerQuarterFrame)
                {
                    _frameCounterTicks -= CpuCyclesPerQuarterFrame;
                    _pulse1.ClockQuarterFrame();
                    _pulse1.ClockHalfFrame();
                    _pulse2.ClockQuarterFrame();
                    _pulse2.ClockHalfFrame();
                }
            }
        }

        public double MixAudio(double apuSample)
        {
            byte p1 = _pulse1.Sample();
            byte p2 = _pulse2.Sample();
            double pulseMix = MixPulseSamples((byte)(p1 + p2));
            double pcmMix = MixPcmSample(_pcm.OutputLevel);
            double mixed = apuSample + pulseMix + pcmMix;
            if (mixed < 0.0) return 0.0;
            if (mixed > 1.0) return 1.0;
            return mixed;
        }

        public void OnPpuScanline(int scanline, bool renderingEnabled)
        {
            if (!_useExternalScanlineCounter)
                return;
            _scanlineCounter.SetScanline(scanline, renderingEnabled);
        }

        public bool IsSaveRamDirty => _saveRamDirty;

        public byte[] GetSaveRam() => _prgRam;

        public void ClearSaveRamDirty() => _saveRamDirty = false;

        private byte ReadInternalRegister(int address, byte openBus)
        {
            switch (address)
            {
                case 0x5010:
                    return _pcm.ReadControl();
                case 0x5015:
                    return (byte)((( _pulse2.LengthCounter > 0 ? 1 : 0) << 1) | (_pulse1.LengthCounter > 0 ? 1 : 0));
                case 0x5204:
                {
                    byte result = (byte)((( _scanlineCounter.IrqPending ? 1 : 0) << 7) | (_scanlineCounter.InFrame ? 1 : 0) << 6);
                    _scanlineCounter.IrqPending = false;
                    return result;
                }
                case 0x5205:
                    return (byte)(_multiplier.Output() & 0x00FF);
                case 0x5206:
                    return (byte)((_multiplier.Output() >> 8) & 0x00FF);
                default:
                    return openBus;
            }
        }

        private void WriteInternalRegister(int address, byte value)
        {
            switch (address)
            {
                case 0x5000:
                    _pulse1.ProcessVolUpdate(value);
                    break;
                case 0x5002:
                    _pulse1.ProcessLoUpdate(value);
                    break;
                case 0x5003:
                    _pulse1.ProcessHiUpdate(value);
                    break;
                case 0x5004:
                    _pulse2.ProcessVolUpdate(value);
                    break;
                case 0x5006:
                    _pulse2.ProcessLoUpdate(value);
                    break;
                case 0x5007:
                    _pulse2.ProcessHiUpdate(value);
                    break;
                case 0x5010:
                    _pcm.ProcessControlUpdate(value);
                    break;
                case 0x5011:
                    _pcm.ProcessRawPcmUpdate(value);
                    break;
                case 0x5015:
                    _pulse1.ProcessSndChnUpdate(value);
                    _pulse2.ProcessSndChnUpdate(value);
                    break;
                case 0x5100:
                    _prgBankingMode = (PrgBankingMode)(value & 0x03);
                    break;
                case 0x5101:
                    _chrMapper.BankSize = (BankSizeKb)(value & 0x03);
                    break;
                case 0x5102:
                    _ramWritesEnabled1 = (value & 0x03) == 0x02;
                    break;
                case 0x5103:
                    _ramWritesEnabled2 = (value & 0x03) == 0x01;
                    break;
                case 0x5104:
                    _extendedRamMode = (ExtendedRamMode)(value & 0x03);
                    break;
                case 0x5105:
                    _nametableMappings[0] = NametableMappingExtensions.FromBits((byte)(value & 0x03));
                    _nametableMappings[1] = NametableMappingExtensions.FromBits((byte)((value >> 2) & 0x03));
                    _nametableMappings[2] = NametableMappingExtensions.FromBits((byte)((value >> 4) & 0x03));
                    _nametableMappings[3] = NametableMappingExtensions.FromBits((byte)((value >> 6) & 0x03));
                    break;
                case 0x5106:
                    _fillModeTileData = value;
                    break;
                case 0x5107:
                {
                    byte paletteIndex = (byte)(value & 0x03);
                    _fillModeAttributes = (byte)(paletteIndex | (paletteIndex << 2) | (paletteIndex << 4) | (paletteIndex << 6));
                    break;
                }
                case 0x5113:
                case 0x5114:
                case 0x5115:
                case 0x5116:
                case 0x5117:
                {
                    byte bank;
                    if (address == 0x5113)
                        bank = (byte)(value & 0x7F);
                    else if (address == 0x5117)
                        bank = (byte)(value | 0x80);
                    else
                        bank = value;

                    _prgBankRegisters[address - 0x5113] = bank;
                    break;
                }
                case 0x5120:
                case 0x5121:
                case 0x5122:
                case 0x5123:
                case 0x5124:
                case 0x5125:
                case 0x5126:
                case 0x5127:
                case 0x5128:
                case 0x5129:
                case 0x512A:
                case 0x512B:
                    _chrMapper.ProcessBankRegisterUpdate(address, value);
                    break;
                case 0x5200:
                    _verticalSplit.Enabled = value.IsBitSet(7);
                    _verticalSplit.Mode = value.IsBitSet(6) ? VerticalSplitMode.Right : VerticalSplitMode.Left;
                    _verticalSplit.SplitTileIndex = (byte)(value & 0x1F);
                    break;
                case 0x5201:
                    _verticalSplit.YScroll = value;
                    break;
                case 0x5202:
                    _verticalSplit.ChrBank = value;
                    break;
                case 0x5203:
                    _scanlineCounter.CompareValue = value;
                    break;
                case 0x5204:
                    _scanlineCounter.IrqEnabled = value.IsBitSet(7);
                    break;
                case 0x5205:
                    _multiplier.OperandL = value;
                    break;
                case 0x5206:
                    _multiplier.OperandR = value;
                    break;
            }
        }

        private byte ReadExtendedRam(int address)
        {
            if (_extendedRamMode == ExtendedRamMode.ReadWrite || _extendedRamMode == ExtendedRamMode.ReadOnly)
                return _extendedRam[address - 0x5C00];
            return _cpuOpenBus;
        }

        private byte ReadChr(int address)
        {
            if (_chrRomSize == 0)
                return 0;
            int idx = address % _chrRomSize;
            return _chrRom[idx];
        }

        private byte ReadChrFromBank(BankSizeKb bankSize, byte bank, int address)
        {
            int sizeBytes = BankSizeKbHelper.ToBytes(bankSize);
            int bankBase = (bank * sizeBytes) % Math.Max(1, _chrRomSize);
            int idx = bankBase + (address % sizeBytes);
            idx %= Math.Max(1, _chrRomSize);
            return _chrRom[idx];
        }

        private byte ReadPrg(int address)
        {
            PrgMapResult map = MapPrgAddress((ushort)address);
            byte value;
            if (map.IsRom)
            {
                int idx = map.Index % Math.Max(1, _prgRomSize);
                value = _prgRom[idx];
            }
            else
            {
                int idx = map.Index % Math.Max(1, _prgRam.Length);
                value = _prgRam[idx];
            }

            _pcm.ProcessCpuRead(address, value);
            return value;
        }

        private void WritePrg(int address, byte value)
        {
            PrgMapResult map = MapPrgAddress((ushort)address);
            if (map.IsRom)
                return;
            int idx = map.Index % Math.Max(1, _prgRam.Length);
            if (_prgRam[idx] != value)
            {
                _prgRam[idx] = value;
                _saveRamDirty = true;
            }
        }

        private bool PrgRamWritesEnabled() => _ramWritesEnabled1 && _ramWritesEnabled2;

        private PrgMapResult MapPrgAddress(ushort address)
        {
            if (address < 0x6000)
                return new PrgMapResult(false, 0);

            if (address <= 0x7FFF)
                return MapResult(_prgBankRegisters[0], BankSizeKb.Eight, address);

            int bankRegister;
            BankSizeKb bankSize;
            switch (_prgBankingMode)
            {
                case PrgBankingMode.Mode0:
                    bankRegister = 4;
                    bankSize = BankSizeKb.ThirtyTwo;
                    break;
                case PrgBankingMode.Mode1:
                    if (address <= 0xBFFF)
                    {
                        bankRegister = 2;
                        bankSize = BankSizeKb.Sixteen;
                    }
                    else
                    {
                        bankRegister = 4;
                        bankSize = BankSizeKb.Sixteen;
                    }
                    break;
                case PrgBankingMode.Mode2:
                    if (address <= 0xBFFF)
                    {
                        bankRegister = 2;
                        bankSize = BankSizeKb.Sixteen;
                    }
                    else if (address <= 0xDFFF)
                    {
                        bankRegister = 3;
                        bankSize = BankSizeKb.Eight;
                    }
                    else
                    {
                        bankRegister = 4;
                        bankSize = BankSizeKb.Eight;
                    }
                    break;
                default:
                    bankRegister = ((address - 0x8000) / 0x2000) + 1;
                    bankSize = BankSizeKb.Eight;
                    break;
            }

            return MapResult(_prgBankRegisters[bankRegister], bankSize, address);
        }

        private PrgMapResult MapResult(byte bankNumber, BankSizeKb bankSize, ushort address)
        {
            bool isRom = (bankNumber & 0x80) != 0;

            if (!isRom && _prgRam.Length == 16 * 1024 && bankSize == BankSizeKb.Eight)
            {
                byte bank = (byte)((bankNumber & 0x04) != 0 ? 1 : 0);
                int ramSizeBytes = BankSizeKbHelper.ToBytes(bankSize);
                int baseAddr = bank * ramSizeBytes;
                int idx = baseAddr + (address % ramSizeBytes);
                return new PrgMapResult(false, idx);
            }

            int maskedBank = isRom ? (bankNumber & 0x7F) : (bankNumber & 0x0F);
            int shiftedBank;
            switch (bankSize)
            {
                case BankSizeKb.Eight: shiftedBank = maskedBank; break;
                case BankSizeKb.Sixteen: shiftedBank = maskedBank >> 1; break;
                case BankSizeKb.ThirtyTwo: shiftedBank = maskedBank >> 2; break;
                default: shiftedBank = maskedBank; break;
            }

            int sizeBytes = BankSizeKbHelper.ToBytes(bankSize);
            int baseIndex = shiftedBank * sizeBytes;
            int index = baseIndex + (address % sizeBytes);
            return new PrgMapResult(isRom, index);
        }

        private static double MixPulseSamples(byte pulseSum)
        {
            if (pulseSum == 0)
                return 0.0;
            double sum = pulseSum;
            return 95.88 / (8128.0 / sum + 100.0);
        }

        private static double MixPcmSample(byte pcm)
        {
            if (pcm == 0)
                return 0.0;
            return 159.79 / (1.0 / (pcm / 22638.0) + 100.0);
        }

        private readonly struct PrgMapResult
        {
            public readonly bool IsRom;
            public readonly int Index;

            public PrgMapResult(bool isRom, int index)
            {
                IsRom = isRom;
                Index = index;
            }
        }

        private enum PrgBankingMode
        {
            Mode0 = 0,
            Mode1 = 1,
            Mode2 = 2,
            Mode3 = 3
        }

        private enum ExtendedRamMode
        {
            Nametable = 0,
            NametableExtendedAttributes = 1,
            ReadWrite = 2,
            ReadOnly = 3
        }

        private enum TileType
        {
            Background,
            Sprite
        }

        private enum NametableMapping
        {
            VramPage0 = 0,
            VramPage1 = 1,
            ExtendedRam = 2,
            FillMode = 3
        }

        private static class NametableMappingExtensions
        {
            public static NametableMapping FromBits(byte bits)
            {
                switch (bits & 0x03)
                {
                    case 0x00: return NametableMapping.VramPage0;
                    case 0x01: return NametableMapping.VramPage1;
                    case 0x02: return NametableMapping.ExtendedRam;
                    case 0x03: return NametableMapping.FillMode;
                    default: return NametableMapping.VramPage0;
                }
            }
        }

        private enum VerticalSplitMode
        {
            Left,
            Right
        }

        private sealed class VerticalSplit
        {
            public bool Enabled;
            public VerticalSplitMode Mode = VerticalSplitMode.Left;
            public byte SplitTileIndex;
            public byte YScroll;
            public byte ChrBank;

            public bool InsideSplit(ScanlineCounter counter)
            {
                if (!Enabled)
                    return false;
                return Mode == VerticalSplitMode.Left
                    ? counter.CurrentTileIndex() < SplitTileIndex
                    : counter.CurrentTileIndex() >= SplitTileIndex;
            }
        }

        private sealed class ScanlineCounter
        {
            public int Scanline;
            public byte CompareValue;
            public bool IrqEnabled;
            public bool IrqPending;
            public bool InFrame;

            private byte _scanlineTileByteFetches;
            private int _lastNametableAddress;
            private byte _sameNametableAddrFetchCount;
            private int _cpuTicksNoRead;
            private int _lastNotifiedScanline = -1;

            public void PreFetch()
            {
                if (_sameNametableAddrFetchCount == 3)
                {
                    _sameNametableAddrFetchCount = 0;
                    if (InFrame)
                    {
                        Scanline++;
                        _scanlineTileByteFetches = 4;
                        if (Scanline == 241)
                        {
                            Scanline = 0;
                            IrqPending = false;
                            InFrame = false;
                        }
                        else if (CompareValue != 0 && Scanline == CompareValue)
                        {
                            IrqPending = true;
                        }
                    }
                    else
                    {
                        Scanline = 0;
                        InFrame = true;
                    }
                }
            }

            public void SetScanline(int scanline, bool renderingEnabled)
            {
                if (!renderingEnabled)
                {
                    InFrame = false;
                    return;
                }

                if (scanline < 0)
                    scanline = 0;
                Scanline = scanline;
                if (_lastNotifiedScanline != Scanline)
                {
                    _lastNotifiedScanline = Scanline;
                    _scanlineTileByteFetches = 4;
                    _sameNametableAddrFetchCount = 0;
                    _lastNametableAddress = 0;
                }
                if (Scanline == 241)
                {
                    InFrame = false;
                    IrqPending = false;
                    return;
                }

                if (!InFrame && Scanline == 0)
                    InFrame = true;

                if (InFrame && CompareValue != 0 && Scanline == CompareValue)
                    IrqPending = true;
            }

            public void IncrementTileBytesFetched()
            {
                _cpuTicksNoRead = 0;
                _scanlineTileByteFetches++;
                if (_scanlineTileByteFetches == 84)
                    _scanlineTileByteFetches = 0;
            }

            public void NametableAddressFetched(int address)
            {
                _cpuTicksNoRead = 0;
                if (_lastNametableAddress == address && _sameNametableAddrFetchCount < 3)
                {
                    _sameNametableAddrFetchCount++;
                }
                else if (_lastNametableAddress != address)
                {
                    _lastNametableAddress = address;
                    _sameNametableAddrFetchCount = 1;
                }
            }

            public void NmiVectorFetched()
            {
                Scanline = 0;
                IrqPending = false;
                InFrame = false;
            }

            public TileType CurrentTileType()
            {
                return _scanlineTileByteFetches < 68 ? TileType.Background : TileType.Sprite;
            }

            public int CurrentTileIndex()
            {
                return _scanlineTileByteFetches / 2;
            }

            public bool InterruptFlag()
            {
                return IrqEnabled && IrqPending;
            }

            public void TickCpu()
            {
                if (_cpuTicksNoRead == 3)
                {
                    InFrame = false;
                    _scanlineTileByteFetches = 4;
                }
                _cpuTicksNoRead++;
            }
        }

        private sealed class ExtendedAttributesState
        {
            public int LastNametableAddr;

            public byte GetAttributeByte(byte[] extendedRam)
            {
                byte extended = extendedRam[LastNametableAddr & 0x03FF];
                byte paletteIndex = (byte)(extended >> 6);
                return (byte)(paletteIndex | (paletteIndex << 2) | (paletteIndex << 4) | (paletteIndex << 6));
            }

            public byte GetPatternTableByte(int patternTableAddr, byte[] extendedRam, byte[] chrRom)
            {
                byte extended = extendedRam[LastNametableAddr & 0x03FF];
                int chr4KbBank = extended & 0x3F;
                int baseAddr = (chr4KbBank * 0x1000) % Math.Max(1, chrRom.Length);
                int idx = baseAddr + (patternTableAddr & 0x0FFF);
                idx %= Math.Max(1, chrRom.Length);
                return chrRom[idx];
            }

            public int GetPatternTableAddress(int patternTableAddr, byte[] extendedRam)
            {
                byte extended = extendedRam[LastNametableAddr & 0x03FF];
                int chr4KbBank = extended & 0x3F;
                int baseAddr = chr4KbBank * 0x1000;
                return baseAddr + (patternTableAddr & 0x0FFF);
            }
        }

        private struct MultiplierUnit
        {
            public ushort OperandL;
            public ushort OperandR;

            public ushort Output() => (ushort)(OperandL * OperandR);
        }

        private enum PcmMode
        {
            Read,
            Write
        }

        private sealed class PcmChannel
        {
            public byte OutputLevel;
            private PcmMode _mode = PcmMode.Write;
            private bool _irqEnabled;
            public bool IrqPending;

            public void ProcessControlUpdate(byte value)
            {
                _mode = (value & 0x01) != 0 ? PcmMode.Read : PcmMode.Write;
                _irqEnabled = (value & 0x80) != 0;
                if (!_irqEnabled)
                    IrqPending = false;
            }

            public byte ReadControl()
            {
                byte control = (byte)(((IrqPending ? 1 : 0) << 7) | (_mode == PcmMode.Read ? 1 : 0));
                IrqPending = false;
                return control;
            }

            public void ProcessRawPcmUpdate(byte value)
            {
                if (_mode == PcmMode.Write)
                {
                    if (value != 0)
                        OutputLevel = value;
                    else if (_irqEnabled)
                        IrqPending = true;
                }
            }

            public void ProcessCpuRead(int address, byte value)
            {
                if (_mode == PcmMode.Read && address >= 0x8000 && address <= 0xBFFF)
                {
                    if (value != 0)
                        OutputLevel = value;
                    else if (_irqEnabled)
                        IrqPending = true;
                }
            }
        }

        private enum BankSizeKb
        {
            Eight = 0,
            Four = 1,
            Two = 2,
            One = 3,
            Sixteen = 16,
            ThirtyTwo = 32
        }

        private static class BankSizeKbHelper
        {
            public static int ToBytes(BankSizeKb size)
            {
                switch (size)
                {
                    case BankSizeKb.One: return 0x0400;
                    case BankSizeKb.Two: return 0x0800;
                    case BankSizeKb.Four: return 0x1000;
                    case BankSizeKb.Eight: return 0x2000;
                    case BankSizeKb.Sixteen: return 0x4000;
                    case BankSizeKb.ThirtyTwo: return 0x8000;
                    default: return 0x2000;
                }
            }
        }

        private sealed class ChrMapper
        {
            public BankSizeKb BankSize = BankSizeKb.Eight;
            private readonly byte[] _bankRegisters = new byte[12];
            private bool _doubleHeightSprites;
            private int _lastRegisterWritten;
            public bool NextAccessFromPpuData;

            public int MapChrAddress(int address, TileType tileType)
            {
                if (NextAccessFromPpuData)
                {
                    NextAccessFromPpuData = false;
                    return _lastRegisterWritten < 8
                        ? MapSpriteChrAddress(address)
                        : MapBgChrAddress(address);
                }

                if (_doubleHeightSprites && tileType == TileType.Background)
                    return MapBgChrAddress(address);

                return MapSpriteChrAddress(address);
            }

            public void ProcessPpuCtrlUpdate(byte value)
            {
                _doubleHeightSprites = value.IsBitSet(5);
            }

            public void ProcessBankRegisterUpdate(int address, byte value)
            {
                int idx = address - 0x5120;
                if (idx < 0 || idx >= _bankRegisters.Length)
                    return;
                _bankRegisters[idx] = value;
                _lastRegisterWritten = idx;
            }

            private int MapSpriteChrAddress(int address)
            {
                switch (BankSize)
                {
                    case BankSizeKb.Eight:
                        return MapChrBank(_bankRegisters[7], BankSizeKb.Eight, address);
                    case BankSizeKb.Four:
                    {
                        int bank = 4 * (address / 0x1000) + 3;
                        return MapChrBank(_bankRegisters[bank], BankSizeKb.Four, address);
                    }
                    case BankSizeKb.Two:
                    {
                        int bank = 2 * (address / 0x0800) + 1;
                        return MapChrBank(_bankRegisters[bank], BankSizeKb.Two, address);
                    }
                    case BankSizeKb.One:
                    {
                        int bank = address / 0x0400;
                        return MapChrBank(_bankRegisters[bank], BankSizeKb.One, address);
                    }
                    default:
                        return address;
                }
            }

            private int MapBgChrAddress(int address)
            {
                switch (BankSize)
                {
                    case BankSizeKb.Eight:
                        return MapChrBank(_bankRegisters[11], BankSizeKb.Eight, address);
                    case BankSizeKb.Four:
                        return MapChrBank(_bankRegisters[11], BankSizeKb.Four, address);
                    case BankSizeKb.Two:
                    {
                        int bank = 2 * ((address & 0x0FFF) / 0x0800) + 9;
                        return MapChrBank(_bankRegisters[bank], BankSizeKb.Two, address);
                    }
                    case BankSizeKb.One:
                    {
                        int bank = (address & 0x0FFF) / 0x0400 + 8;
                        return MapChrBank(_bankRegisters[bank], BankSizeKb.One, address);
                    }
                    default:
                        return address;
                }
            }

            private static int MapChrBank(byte bank, BankSizeKb size, int address)
            {
                int sizeBytes = BankSizeKbHelper.ToBytes(size);
                int baseAddr = bank * sizeBytes;
                return baseAddr + (address % sizeBytes);
            }
        }

        private sealed class PulseChannel
        {
            private readonly PhaseTimer _timer = new PhaseTimer(8, 2, 11, true);
            private DutyCycle _duty = DutyCycle.OneEighth;
            private readonly LengthCounter _length = new LengthCounter(0x01);
            private readonly Envelope _envelope = new Envelope();

            public void ProcessVolUpdate(byte value)
            {
                _duty = DutyCycleHelper.FromVol(value);
                _length.ProcessVolUpdate(value);
                _envelope.ProcessVolUpdate(value);
            }

            public void ProcessLoUpdate(byte value)
            {
                _timer.ProcessLoUpdate(value);
            }

            public void ProcessHiUpdate(byte value)
            {
                _timer.ProcessHiUpdate(value);
                _length.ProcessHiUpdate(value);
                _envelope.ProcessHiUpdate();
            }

            public void ProcessSndChnUpdate(byte value)
            {
                _length.ProcessSndChnUpdate(value);
            }

            public void ClockQuarterFrame() => _envelope.Clock();
            public void ClockHalfFrame() => _length.Clock();
            public void TickCpu() => _timer.Tick(true);

            public byte Sample()
            {
                if (_length.Counter == 0)
                    return 0;
                byte wave = DutyCycleHelper.Waveform(_duty)[_timer.Phase];
                return (byte)(wave * _envelope.Volume);
            }

            public byte LengthCounter => _length.Counter;
        }

        private sealed class Envelope
        {
            private byte _divider;
            private byte _dividerPeriod;
            private byte _decayLevel;
            private bool _startFlag;
            private bool _loopFlag;
            private bool _constantVolume;

            public byte Volume => _constantVolume ? _dividerPeriod : _decayLevel;

            public void ProcessVolUpdate(byte value)
            {
                _loopFlag = value.IsBitSet(5);
                _constantVolume = value.IsBitSet(4);
                _dividerPeriod = (byte)(value & 0x0F);
            }

            public void ProcessHiUpdate()
            {
                _startFlag = true;
            }

            public void Clock()
            {
                if (_startFlag)
                {
                    _startFlag = false;
                    _divider = _dividerPeriod;
                    _decayLevel = 0x0F;
                }
                else if (_divider == 0)
                {
                    _divider = _dividerPeriod;
                    if (_decayLevel > 0)
                        _decayLevel--;
                    else if (_loopFlag)
                        _decayLevel = 0x0F;
                }
                else
                {
                    _divider--;
                }
            }
        }

        private sealed class LengthCounter
        {
            private static readonly byte[] Table =
            {
                10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
                12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30
            };

            private readonly byte _mask;
            public byte Counter;
            private bool _enabled;
            private bool _halted;

            public LengthCounter(byte mask)
            {
                _mask = mask;
            }

            public void ProcessSndChnUpdate(byte value)
            {
                bool enabled = (value & _mask) != 0;
                _enabled = enabled;
                if (!enabled) Counter = 0;
            }

            public void ProcessVolUpdate(byte value)
            {
                _halted = value.IsBitSet(5);
            }

            public void ProcessHiUpdate(byte value)
            {
                if (_enabled)
                    Counter = Table[(value >> 3) & 0x1F];
            }

            public void Clock()
            {
                if (!_halted && Counter > 0)
                    Counter--;
            }
        }

        private sealed class PhaseTimer
        {
            private readonly byte _maxPhase;
            private readonly byte _cpuTicksPerClock;
            private readonly byte _dividerBits;
            private readonly bool _canResetPhase;

            private byte _cpuTicks;
            private ushort _cpuDivider;
            public ushort DividerPeriod;
            public byte Phase;

            public PhaseTimer(byte maxPhase, byte cpuTicksPerClock, byte dividerBits, bool canResetPhase)
            {
                _maxPhase = maxPhase;
                _cpuTicksPerClock = cpuTicksPerClock;
                _dividerBits = dividerBits;
                _canResetPhase = canResetPhase;
            }

            public void ProcessLoUpdate(byte value)
            {
                DividerPeriod = (ushort)((DividerPeriod & 0xFF00) | value);
            }

            public void ProcessHiUpdate(byte value)
            {
                ushort mask = _dividerBits == 11 ? (ushort)0x07 : (ushort)0x0F;
                DividerPeriod = (ushort)(((value & mask) << 8) | (DividerPeriod & 0x00FF));
                if (_canResetPhase)
                    Phase = 0;
            }

            public void Tick(bool sequencerEnabled)
            {
                _cpuTicks++;
                if (_cpuTicks < _cpuTicksPerClock)
                    return;
                _cpuTicks = 0;

                if (_cpuDivider == 0)
                {
                    _cpuDivider = DividerPeriod;
                    if (sequencerEnabled)
                        Phase = (byte)((Phase + 1) & (_maxPhase - 1));
                }
                else
                {
                    _cpuDivider--;
                }
            }
        }

        private enum DutyCycle
        {
            OneEighth,
            OneFourth,
            OneHalf,
            ThreeFourths
        }

        private static class DutyCycleHelper
        {
            private static readonly byte[] WaveOneEighth = { 0, 1, 0, 0, 0, 0, 0, 0 };
            private static readonly byte[] WaveOneFourth = { 0, 1, 1, 0, 0, 0, 0, 0 };
            private static readonly byte[] WaveOneHalf = { 0, 1, 1, 1, 1, 0, 0, 0 };
            private static readonly byte[] WaveThreeFourths = { 1, 0, 0, 1, 1, 1, 1, 1 };

            public static DutyCycle FromVol(byte value)
            {
                switch (value & 0xC0)
                {
                    case 0x00: return DutyCycle.OneEighth;
                    case 0x40: return DutyCycle.OneFourth;
                    case 0x80: return DutyCycle.OneHalf;
                    case 0xC0: return DutyCycle.ThreeFourths;
                    default: return DutyCycle.OneEighth;
                }
            }

            public static byte[] Waveform(DutyCycle duty)
            {
                switch (duty)
                {
                    case DutyCycle.OneEighth: return WaveOneEighth;
                    case DutyCycle.OneFourth: return WaveOneFourth;
                    case DutyCycle.OneHalf: return WaveOneHalf;
                    case DutyCycle.ThreeFourths: return WaveThreeFourths;
                    default: return WaveOneEighth;
                }
            }
        }
    }
}
