using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Common.Extensions;

namespace XamariNES.Cartridge.Mappers.impl
{
    /// <summary>
    ///     NES Mapper 1 (MMC1)
    ///
    ///     More Info: https://wiki.nesdev.com/w/index.php/MMC1
    /// </summary>
    public class MMC1 : MapperBase, IMapper, IMapperOpenBusRead, ISaveRamProvider, IMapperCpuTick
    {
        /// <summary>
        ///     PRG ROM
        ///
        ///     256kb Capacity
        /// </summary>
        [NonSerialized]
        private readonly byte[] _prgRom;

        /// <summary>
        ///     Number of PRG ROM Banks on this Cartridge
        /// </summary>
        private readonly int _prgRomBanks;

        /// <summary>
        ///     PRG RAM
        ///
        ///     32kb Capacity
        /// </summary>
        private readonly byte[] _prgRam;
        private bool _saveRamDirty;
        public bool BatteryBacked { get; }

        /// <summary>
        ///     CHR ROM
        ///
        ///     128kb Capacity
        /// </summary>
        [NonSerialized]
        private readonly byte[] _chrRom;

        //Registers
        private int _registerShift;
        private int _registerShiftOffset;
        private int _registerControl;
        private int _chrBank0;
        private int _chrBank1;
        private int _prgBank;

        //Bank Switching Modes for CHR and PRG
        private int _currentPrgMode;
        private int _currentChrMode;

        //Current Offset of the banks in the total bank memory space
        private int _chrBank0Offset;
        private int _chrBank1Offset;
        private int _prgBank0Offset;
        private int _prgBank1Offset;

        //Toggles for RAM
        private readonly bool _useChrRam;
        private readonly bool _hasPrgRam;
        private bool _prgRamEnabled;
        [NonSerialized]
        private bool _loadWriteSeenThisInstruction;
        [NonSerialized]
        private readonly bool _traceMmc1 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NES_MMC1"), "1", StringComparison.Ordinal);
        [NonSerialized]
        private readonly int _traceMmc1Limit = ParseTraceLimit("EUTHERDRIVE_TRACE_NES_MMC1_LIMIT", 4000);
        [NonSerialized]
        private int _traceMmc1Count;

        public enumNametableMirroring NametableMirroring { get; set; }

        public MMC1(int prgRomBanks, byte[] chrRom, byte[] prgRom, bool useChrRam, bool usePrgRam, int prgRamSize, bool batteryBacked,
            enumNametableMirroring mirroring = enumNametableMirroring.Horizontal)
        {
            _prgRomBanks = prgRomBanks;
            _chrRom = chrRom;
            _prgRom = prgRom;
            NametableMirroring = mirroring;
            _useChrRam = useChrRam;
            _hasPrgRam = usePrgRam;
            _prgRamEnabled = usePrgRam;
            _prgRam = new byte[Math.Max(1, prgRamSize)];
            BatteryBacked = batteryBacked;

            // MMC1 power-on: control defaults to $0C while shift register starts empty.
            _registerShift = 0x00;
            _registerShiftOffset = 0;
            _registerControl = 0x0C;
            _currentPrgMode = 3;
            _currentChrMode = 0;
            _chrBank0 = 0;
            _chrBank1 = 0;
            _prgBank = 0;
            UpdateBankOffsets();
        }

        public byte ReadByte(int offset, byte cpuOpenBus)
        {
            if (offset >= 0x4020 && offset <= 0x5FFF)
                return cpuOpenBus;
            if (offset >= 0x6000 && offset <= 0x7FFF && (!_hasPrgRam || !_prgRamEnabled))
                return cpuOpenBus;
            return ReadByte(offset);
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
            // CHR Bank 0 == $0000-$0FFF
            // CHR Bank 1 == $1000-$1FFF
            if (offset <= 0x1FFF)
            {
                var chrBankOffset = offset / 0x1000 == 0 ? _chrBank0Offset : _chrBank1Offset;
                chrBankOffset += offset % 0x1000;
                if ((uint)chrBankOffset >= (uint)_chrRom.Length)
                    chrBankOffset = ((chrBankOffset % _chrRom.Length) + _chrRom.Length) % _chrRom.Length;
                return _chrRom[chrBankOffset];
            }

            //PPU Registers
            if (offset <= 0x3FFF)
                return ReadInterceptors.TryGetValue(offset, out currentReadInterceptor) ? currentReadInterceptor(offset) : (byte) 0x0;

            // Normally-disabled APU/I/O range ($4020-$5FFF) is open bus/board-specific; ignore for now.
            if (offset <= 0x5FFF)
                return 0x00;

            // PRG RAM Bank == $6000-$7FFF
            if (offset >= 0x6000 && offset <= 0x7FFF)
            {
                if (!_hasPrgRam || !_prgRamEnabled)
                    return 0x00;

                return _prgRam[(offset - 0x6000) % _prgRam.Length];
            }

            // PRG Bank 0 == $8000-$BFFF
            // PRG Bank 1 == $C000-$FFFF
            if (offset >= 0x8000 && offset <= 0xFFFF)
            {
                //Map address $8000 to our _prgRom start of 0x0000;
                var prgBaseOffset = offset - 0x8000;
                var prgBankOffset = prgBaseOffset / 0x4000 == 0 ? _prgBank0Offset : _prgBank1Offset;
                prgBankOffset += prgBaseOffset % 0x4000;
                if ((uint)prgBankOffset >= (uint)_prgRom.Length)
                    prgBankOffset = ((prgBankOffset % _prgRom.Length) + _prgRom.Length) % _prgRom.Length;
                return _prgRom[prgBankOffset];
            }

            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Maximum value of offset is 0xFFFF");
        }

        /// <summary>
        ///     Writes one byte to the specified bank, at the specified offset
        /// </summary>
        /// <param name="memoryType"></param>
        /// <param name="offset"></param>
        /// <param name="data"></param>
        public void WriteByte(int offset, byte data)
        {
            offset &= 0xFFFF;
            // CHR Bank 0 == $0000-$0FFF
            // CHR Bank 1 == $1000-$1FFF
            if (offset <= 0x1FFF)
            {
                if(!_useChrRam)
                    return;

                var chrOffset = (offset / 0x1000) == 0 ? _chrBank0Offset : _chrBank1Offset;
                chrOffset += offset % 0x1000;
                if ((uint)chrOffset >= (uint)_chrRom.Length)
                    chrOffset = ((chrOffset % _chrRom.Length) + _chrRom.Length) % _chrRom.Length;
                _chrRom[chrOffset] = data;
                return;
            }

            //PPU Registers
            if (offset <= 0x3FFF || offset == 0x4014)
            {
                if (WriteInterceptors.TryGetValue(offset, out currentWriteInterceptor))
                    currentWriteInterceptor(offset, data);

                return;
            }

            // Normally-disabled APU/I/O range ($4020-$5FFF) is board-specific; ignore writes.
            if (offset <= 0x5FFF)
                return;

            // PRG RAM Bank == $6000-$7FFF
            if (offset >= 0x6000 && offset <= 0x7FFF)
            {
                if (!_hasPrgRam || !_prgRamEnabled)
                    return;

                int idx = (offset - 0x6000) % _prgRam.Length;
                if (_prgRam[idx] != data)
                {
                    _prgRam[idx] = data;
                    _saveRamDirty = true;
                }
                return;
            }

            //Writes to this range are handled by the Load Register
            if (offset >= 0x8000 && offset <= 0xFFFF)
            {
                WriteLoadRegister(offset, data);
                return;
            }

            //Sanity Check if we reach this point
            throw new ArgumentOutOfRangeException(nameof(offset), "Maximum value of offset is 0xFFFF");
        }

        /// <summary>
        ///     Load Register is mapped to $8000->$FFFF in the Mapper Memory Space
        ///
        ///     From there, the Load Register processes the write one bit at a time shifting left, until 5 writes
        ///     at which point we write to the internal register mapped to the given address of the final write.
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="data"></param>
        private void WriteLoadRegister(int offset, byte data)
        {
            TraceMmc1($"[NES-MMC1-W] off=0x{offset & 0xFFFF:X4} data=0x{data:X2} sh=0x{_registerShift:X2} shofs={_registerShiftOffset} seen={(_loadWriteSeenThisInstruction ? 1 : 0)}");

            if (data.IsBitSet(7))
            {
                _loadWriteSeenThisInstruction = true;
                //Write Control with (Control OR $0C),
                //locking PRG ROM at $C000-$FFFF to the last bank.
                _registerShift = _registerControl | 0x0C;
                WriteInternalRegister(0x0);

                //Reset Shift Register
                _registerShiftOffset = 0;
                _registerShift = 0;
                return;
            }

            // MMC1 ignores back-to-back load register writes generated by many RMW sequences.
            // In this core, use instruction-granularity filtering.
            if (_loadWriteSeenThisInstruction)
                return;
            _loadWriteSeenThisInstruction = true;

            _registerShift |= (data & 1) << _registerShiftOffset;
            _registerShiftOffset++;

            //5th write gets written to the internal registers
            if (_registerShiftOffset == 5)
            {
                _registerShiftOffset = 0;
                WriteInternalRegister(offset);
                _registerShift = 0;
            }
        }

        /// <summary>
        ///     Determines, based on the offset of the write, which internal register
        ///     will be written to:
        ///
        ///     Internal Registers:
        ///     $8000->$9FFF == Control Register
        ///     $A000->$BFFF == CHR0 Register
        ///     $C000->$DFFF == CHR1 Register
        ///     $E000->$FFFF == PRG Register
        /// </summary>
        /// <param name="offset"></param>
        private void WriteInternalRegister(int offset)
        {
            if (offset <= 0x9FFF)
            {
                _registerControl = _registerShift;

                _currentPrgMode = (_registerShift >> 2) & 0x03;
                _currentChrMode = (_registerShift >> 4) & 0x01;
                switch (_registerControl & 0x03)
                {
                    case 0:
                        NametableMirroring = enumNametableMirroring.SingleLower;
                        break;
                    case 1:
                        NametableMirroring = enumNametableMirroring.SingleUpper;
                        break;
                    case 2:
                        NametableMirroring = enumNametableMirroring.Vertical;
                        break;
                    case 3:
                        NametableMirroring = enumNametableMirroring.Horizontal;
                        break;
                }
            }
            else if (offset <= 0xBFFF)
            {
                _chrBank0 = _registerShift;
            }
            else if (offset <= 0xDFFF)
            {
                _chrBank1 = _registerShift;
            }
            else
            {
                _prgBank = _registerShift;
                // Compatibility: many MMC1 boards ignore/repurpose bit 4.
                // Keep PRG RAM enabled when the cartridge has PRG RAM.
                _prgRamEnabled = _hasPrgRam;
            }

            //Based off this write, update the offsets of the PRG and CHR Banks
            UpdateBankOffsets();
            TraceMmc1(
                $"[NES-MMC1-R] off=0x{offset & 0xFFFF:X4} ctrl=0x{_registerControl:X2} chr0=0x{_chrBank0:X2} chr1=0x{_chrBank1:X2} prg=0x{_prgBank:X2} cm={_currentChrMode} pm={_currentPrgMode} ramEn={(_prgRamEnabled ? 1 : 0)} cbo=0x{_chrBank0Offset:X04} cb1=0x{_chrBank1Offset:X04} p0=0x{_prgBank0Offset:X05} p1=0x{_prgBank1Offset:X05}");
        }

        public void TickCpu(int cycles)
        {
            _loadWriteSeenThisInstruction = false;
        }

        private void TraceMmc1(string line)
        {
            if (!_traceMmc1 || _traceMmc1Count >= _traceMmc1Limit)
                return;
            Console.WriteLine(line);
            if (_traceMmc1Count != int.MaxValue)
                _traceMmc1Count++;
        }

        private static int ParseTraceLimit(string name, int fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return fallback;
            return value <= 0 ? int.MaxValue : value;
        }

        /// <summary>
        ///     Updates the offsets for CHR0, CHR1, and PRG based off updates to
        ///     the control registers, or any of the other registers.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateBankOffsets()
        {
            int chrBanks4k = Math.Max(1, _chrRom.Length / 0x1000);
            int prgBanks16k = Math.Max(1, _prgRom.Length / 0x4000);

            switch (_currentChrMode)
            {
                case 0:
                    //8K (4K+4K contiguous)
                    _chrBank0Offset = ((_chrBank0 & 0x1E) * 0x1000) % _chrRom.Length;
                    _chrBank1Offset = _chrBank0Offset + 0x1000;
                    if (_chrBank1Offset >= _chrRom.Length)
                        _chrBank1Offset %= _chrRom.Length;
                    break;
                case 1:
                    //4K Switched + 4K Switched
                    _chrBank0Offset = (_chrBank0 % chrBanks4k) * 0x1000;
                    _chrBank1Offset = (_chrBank1 % chrBanks4k) * 0x1000;
                    break;
                default:
                    throw new ArgumentException("Invalid CHR Mode Specified");
            }

            switch (_currentPrgMode)
            {
                case 0:
                case 1: //32KB (16KB+16KB contiguous) Switched
                    _prgBank0Offset = (((_prgBank & 0xE) >> 1) % Math.Max(1, prgBanks16k / 2)) * 0x4000;
                    _prgBank1Offset = _prgBank0Offset + 0x4000;
                    if (_prgBank1Offset >= _prgRom.Length)
                        _prgBank1Offset %= _prgRom.Length;
                    break;
                case 2: //16KB Fixed (First) + 16KB Switched
                    //Fixed first bank at $8000
                    _prgBank0Offset = 0;
                    //Switched 16KB bank at $C000
                    _prgBank1Offset = ((_prgBank & 0xF) % prgBanks16k) * 0x4000;
                    break;
                case 3: //16KB Switched + 16KB Fixed (Last)
                    //Switched 16 KB bank at $8000
                    _prgBank0Offset = ((_prgBank & 0xF) % prgBanks16k) * 0x4000;
                    //Fixed last bank at $C000
                    _prgBank1Offset = (prgBanks16k - 1) * 0x4000;
                    break;
                default:
                    throw new ArgumentException("Invalid PRG Mode Specified");
            }
        }

        public bool IsSaveRamDirty => _saveRamDirty;

        public byte[] GetSaveRam() => _prgRam;

        public void ClearSaveRamDirty()
        {
            _saveRamDirty = false;
        }
    }
}
