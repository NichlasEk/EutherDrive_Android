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
    public class MMC3 : MapperBase, IMapper, IMapperOpenBusRead, IMapperIrqProvider, IPpuA12Observer, IMapperScanlineCounter, ISaveRamProvider
    {
        [NonSerialized]
        private readonly byte[] _prgRom;
        [NonSerialized]
        private readonly byte[] _chrRom;
        private readonly byte[] _prgRam;
        private bool _saveRamDirty;
        public bool BatteryBacked { get; }
        private readonly bool _useChrRam;
        private readonly bool _isTqrom;
        private readonly byte[] _tqromChrRam;
        private readonly int _prgBankCount8k;
        private readonly int _chrBankCount1k;
        private byte _bankSelect;
        private readonly byte[] _bankRegs = new byte[8];
        private bool _prgMode;
        private bool _chrMode;
        private bool _prgRamEnabled = true;
        private bool _prgRamWriteProtect;

        private byte _irqLatch;
        private byte _irqCounter;
        private bool _irqReload;
        private bool _irqEnabled;
        private bool _irqPending;
        private bool _lastA12;
        private long _lastA12LowCycle = -100000;
        private readonly bool _useScanlineClock;
        [NonSerialized]
        private readonly bool _traceTqromChrRamWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NES_TQROM_CHR_RAM"), "1", StringComparison.Ordinal);
        [NonSerialized]
        private readonly int _traceTqromChrRamWritesLimit = 4000;
        [NonSerialized]
        private int _traceTqromChrRamWritesCount;
        [NonSerialized]
        private readonly bool _traceMmc3Banks =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NES_MMC3_BANKS"), "1", StringComparison.Ordinal);
        [NonSerialized]
        private readonly int _traceMmc3BanksLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_NES_MMC3_BANKS_LIMIT", 512);
        [NonSerialized]
        private int _traceMmc3BanksCount;
        [NonSerialized]
        private readonly bool _traceTqromChrReads =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NES_TQROM_CHR_READS"), "1", StringComparison.Ordinal);
        [NonSerialized]
        private readonly int _traceTqromChrReadsLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_NES_TQROM_CHR_READS_LIMIT", 2048);
        [NonSerialized]
        private readonly long _traceTqromChrReadsStartCycle = ParseTraceStartCycle("EUTHERDRIVE_TRACE_NES_TQROM_CHR_READS_START_CYCLE");
        [NonSerialized]
        private int _traceTqromChrReadsCount;
        [NonSerialized]
        private long _lastPpuCycleObserved = long.MinValue;

        public enumNametableMirroring NametableMirroring { get; set; }

        public bool IrqPending => _irqPending;

        public MMC3(byte[] prgRom, byte[] chrRom, bool useChrRam, int prgRamSize, bool batteryBacked,
            enumNametableMirroring mirroring = enumNametableMirroring.Horizontal, int mapperNumber = 4)
        {
            _prgRom = prgRom;
            _chrRom = chrRom;
            _useChrRam = useChrRam;
            _isTqrom = mapperNumber == 119;
            _tqromChrRam = _isTqrom ? new byte[0x2000] : Array.Empty<byte>();
            _prgRam = new byte[Math.Max(1, prgRamSize)];
            _prgBankCount8k = Math.Max(1, _prgRom.Length / 0x2000);
            _chrBankCount1k = Math.Max(1, _chrRom.Length / 0x0400);
            _useScanlineClock = !_isTqrom;
            NametableMirroring = mirroring;
            BatteryBacked = batteryBacked;
        }

        public byte ReadByte(int offset, byte cpuOpenBus)
        {
            if (offset >= 0x4020 && offset <= 0x5FFF)
                return cpuOpenBus;

            if (offset >= 0x6000 && offset <= 0x7FFF && !_prgRamEnabled)
                return cpuOpenBus;

            return ReadByte(offset);
        }

        public byte ReadByte(int offset)
        {
            if (offset <= 0x1FFF)
            {
                if (_isTqrom)
                {
                    int rawBank = ResolveChrBankRaw(offset);
                    int bankOffset = offset & 0x03FF;
                    TraceTqromChrRead(offset, rawBank, bankOffset);
                    if ((rawBank & 0x40) != 0)
                    {
                        int ramBank = rawBank & 0x07;
                        return _tqromChrRam[(ramBank * 0x0400) + bankOffset];
                    }

                    int romBank = (rawBank & 0x3F) % _chrBankCount1k;
                    return _chrRom[(romBank * 0x0400) + bankOffset];
                }
                else
                {
                    int chrIndex = ResolveChrAddress(offset);
                    return _chrRom[chrIndex];
                }
            }

            if (offset <= 0x3FFF)
                return ReadInterceptors.TryGetValue(offset, out currentReadInterceptor) ? currentReadInterceptor(offset) : (byte)0x00;

            if (offset >= 0x6000 && offset <= 0x7FFF)
            {
                if (_prgRamEnabled)
                    return _prgRam[offset - 0x6000];
                return 0x00;
            }

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
                if (_isTqrom)
                {
                    int rawBank = ResolveChrBankRaw(offset);
                    if ((rawBank & 0x40) == 0)
                        return; // CHR ROM selected

                    if (_traceTqromChrRamWrites && _traceTqromChrRamWritesCount < _traceTqromChrRamWritesLimit && data != 0)
                    {
                        Console.WriteLine($"[NES-TQROM-RAM] {DebugTraceContext.FormatCpu()} ppu=0x{offset:X4} raw=0x{rawBank:X2} data=0x{data:X2}");
                        if (_traceTqromChrRamWritesCount != int.MaxValue)
                            _traceTqromChrRamWritesCount++;
                    }
                    int bankOffset = offset & 0x03FF;
                    int ramBank = rawBank & 0x07;
                    _tqromChrRam[(ramBank * 0x0400) + bankOffset] = data;
                    return;
                }

                if (!_useChrRam)
                    return;
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

            // Cartridge expansion area ($4020-$5FFF) is mapper/board-specific.
            // Treat unmapped writes as no-op instead of throwing.
            if (offset >= 0x4020 && offset <= 0x5FFF)
                return;

            if (offset >= 0x6000 && offset <= 0x7FFF)
            {
                if (!_prgRamEnabled || _prgRamWriteProtect)
                    return;

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
                    TraceMmc3BankWrite(offset, data, "sel");
                    break;
                case 0x8001:
                {
                    int reg = _bankSelect & 0x07;
                    _bankRegs[reg] = data;
                    TraceMmc3BankWrite(offset, data, $"r{reg}");
                    break;
                }
                case 0xA000:
                    NametableMirroring = data.IsBitSet(0)
                        ? enumNametableMirroring.Horizontal
                        : enumNametableMirroring.Vertical;
                    TraceMmc3BankWrite(offset, data, "mir");
                    break;
                case 0xA001:
                    _prgRamWriteProtect = data.IsBitSet(6);
                    _prgRamEnabled = data.IsBitSet(7);
                    TraceMmc3BankWrite(offset, data, "ram");
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
            int bankIndex = ResolveChrBankRaw(offset);
            bankIndex %= _chrBankCount1k;
            int bankOffset = offset & 0x03FF;
            return bankIndex * 0x0400 + bankOffset;
        }

        private int ResolveChrBankRaw(int offset)
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

            return bankIndex;
        }

        public void NotifyPpuA12(int ppuAddress, long ppuCycle)
        {
            _lastPpuCycleObserved = ppuCycle;
            if (_useScanlineClock)
                return;
            UpdateA12(ppuAddress, ppuCycle);
        }

        private void UpdateA12(int ppuAddress, long ppuCycle)
        {
            bool a12 = (ppuAddress & 0x1000) != 0;
            if (!a12 && _lastA12)
                _lastA12LowCycle = ppuCycle;

            if (!_lastA12 && a12 && ppuCycle - _lastA12LowCycle >= 12)
                ClockIrqCounter();

            _lastA12 = a12;
        }

        public void ClockScanline()
        {
            if (!_useScanlineClock)
                return;

            ClockIrqCounter();
        }

        private void ClockIrqCounter()
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

        private void TraceMmc3BankWrite(int offset, byte data, string kind)
        {
            if (!_traceMmc3Banks || _traceMmc3BanksCount >= _traceMmc3BanksLimit)
                return;

            Console.WriteLine(
                $"[NES-MMC3] {DebugTraceContext.FormatCpu()} kind={kind} addr=0x{offset:X4} data=0x{data:X2} chrMode={(_chrMode ? 1 : 0)} prgMode={(_prgMode ? 1 : 0)} regs={_bankRegs[0]:X2},{_bankRegs[1]:X2},{_bankRegs[2]:X2},{_bankRegs[3]:X2},{_bankRegs[4]:X2},{_bankRegs[5]:X2},{_bankRegs[6]:X2},{_bankRegs[7]:X2}");
            if (_traceMmc3BanksCount != int.MaxValue)
                _traceMmc3BanksCount++;
        }

        private void TraceTqromChrRead(int offset, int rawBank, int bankOffset)
        {
            if (!_traceTqromChrReads ||
                _lastPpuCycleObserved < _traceTqromChrReadsStartCycle ||
                _traceTqromChrReadsCount >= _traceTqromChrReadsLimit)
                return;

            Console.WriteLine(
                $"[NES-TQROM-READ] {DebugTraceContext.FormatCpu()} ppu=0x{offset:X4} cycle={_lastPpuCycleObserved} raw=0x{rawBank:X2} slot={(offset >> 10) & 0x07} offs=0x{bankOffset:X3} src={(((rawBank & 0x40) != 0) ? "ram" : "rom")} chrMode={(_chrMode ? 1 : 0)} regs={_bankRegs[0]:X2},{_bankRegs[1]:X2},{_bankRegs[2]:X2},{_bankRegs[3]:X2},{_bankRegs[4]:X2},{_bankRegs[5]:X2}");
            if (_traceTqromChrReadsCount != int.MaxValue)
                _traceTqromChrReadsCount++;
        }

        private static int ParseTraceLimit(string envName, int fallback)
        {
            string raw = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            return int.TryParse(raw.Trim(), out int value)
                ? (value <= 0 ? int.MaxValue : value)
                : fallback;
        }

        private static long ParseTraceStartCycle(string envName)
        {
            string raw = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(raw))
                return long.MinValue;

            return long.TryParse(raw.Trim(), out long value) ? value : long.MinValue;
        }
    }
}
