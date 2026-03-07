using System;
using System.IO;
using NLog;
using XamariNES.Cartridge.Flags;
using XamariNES.Cartridge.Mappers;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Cartridge.Mappers.impl;
using XamariNES.Common.Extensions;
using XamariNES.Common.Logging;

namespace XamariNES.Cartridge
{
    /// <summary>
    ///     Class that Represents a NES cartridge by loading an iNES format ROM
    ///
    ///     This class/project will contain the PGR/CHR memory as well as mapper functionality.
    ///     Access to this class is abstracted through the ICartridge interface, which is referenced
    ///     directly by both the CPU and PPU (as in the actual NES)
    ///
    ///     ROM Format: https://wiki.nesdev.com/w/index.php/INES
    /// </summary>
    public class NESCartridge : ICartridge
    {
        protected static readonly Logger _logger = LogManager.GetCurrentClassLogger(typeof(CustomLogger));

        private byte Flags6;
        private byte Flags7;
        private byte[] _prgRom;
        private byte _prgRomBanks;
        private byte[] _chrRom;
        private byte _chrRomBanks;
        private byte[] _prgRam;
        private bool UsesCHRRAM;
        private enumNametableMirroring _nametableMirroring;

        public IMapper MemoryMapper { get; set; }

        /// <summary>
        ///     Constructor
        /// </summary>
        /// <param name="path">Path to the desired iNES ROM to load</param>
        public NESCartridge(string path)
        {
            if (!File.Exists(path))
                throw new Exception($"Unable to Load ROM: {path}");

            if (new FileInfo(path).Length > int.MaxValue)
                throw new Exception($"Unsupported ROM - File size too large: {path}");

            LoadROM(File.ReadAllBytes(path));
        }

        /// <summary>
        ///     Constructor
        /// </summary>
        /// <param name="ROM">Byte Array containing the desired iNES ROM to load</param>
        public NESCartridge(byte[] ROM) => LoadROM(ROM);

        /// <summary>
        ///     Loads the specified iNES ROM
        /// </summary>
        /// <param name="ROM">Byte Array containing the desired iNES ROM to load</param>
        /// <returns>TRUE if load was successful</returns>
        public bool LoadROM(byte[] ROM)
        {
            //Header is 16 bytes

            //PRG Rom starts right after, unless there's a 512 byte trainer (indicated by flags)
            var prgROMOffset = 16;

            //_header == "NES<EOF>"
            if (BitConverter.ToInt32(ROM, 0) != 0x1A53454E)
                throw new Exception("Invalid ROM Header");

            //Setup Memory
            _prgRomBanks = ROM[4];
            var prgROMSize = _prgRomBanks * 16384;
            _prgRom = new byte[prgROMSize];
            _logger.Info($"PRG ROM Size: {prgROMSize}");

            _chrRomBanks = ROM[5];
            var chrROMSize = _chrRomBanks * 8192; //0 denotes default 8k
            if (ROM[5] == 0)
            {
                _chrRom = new byte[8192];
                UsesCHRRAM = true;
            }
            else
            {
                _chrRom = new byte[chrROMSize];
            }
            _logger.Info($"CHR ROM Size: {chrROMSize}");

            //Set Flags6
            Flags6 = ROM[6];
            bool batteryBacked = Flags6.IsFlagSet(Byte6Flags.BatteryBackedPRGRAM);

            //Move PGR ROM Start if Trainer Present
            if (Flags6.IsFlagSet(Byte6Flags.TrainerPresent))
                prgROMOffset += 512;

            //Set Initial Mirroring Mode
            _nametableMirroring = Flags6.IsFlagSet(Byte6Flags.VerticalMirroring) ? enumNametableMirroring.Vertical : enumNametableMirroring.Horizontal;

            //Set Flags7
            Flags7 = ROM[7];

            var prgRAMSize = ROM[8] == 0 ? 8192 : ROM[8] * 8192; //0 denoted default 8k
            _prgRam = new byte[prgRAMSize];
            bool usePrgRam = prgRAMSize > 0;

            //Load PRG ROM
            Array.Copy(ROM, prgROMOffset, _prgRom, 0, prgROMSize);

            //Load CHR ROM
            Array.Copy(ROM, prgROMOffset+prgROMSize, _chrRom, 0, chrROMSize);

            // Load mapper number/submapper (supports NES 2.0 submapper for mapper variants).
            bool isNes2 = (Flags7 & 0x0C) == 0x08;
            int mapperNumber = Flags7 & 0xF0 | (Flags6 >> 4 & 0xF);
            int subMapperNumber = 0;
            if (isNes2 && ROM.Length > 8)
            {
                mapperNumber |= (ROM[8] & 0x0F) << 8;
                subMapperNumber = (ROM[8] >> 4) & 0x0F;
            }

            // iNES headers usually don't specify mapper 30 CHR RAM size; use 32KB default.
            if (mapperNumber == 30 && UsesCHRRAM && _chrRom.Length < 32768)
            {
                _chrRom = new byte[32768];
            }
            _logger.Info($"NES header parsed: isNes2={isNes2} mapper={mapperNumber} subMapper={subMapperNumber}");
            if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NES_M16"), "1", StringComparison.Ordinal))
                Console.WriteLine($"[NES-HDR] isNes2={isNes2} mapper={mapperNumber} subMapper={subMapperNumber}");

            //Load Proper Mapper
            switch (mapperNumber)
            {
                case 0:
                    MemoryMapper = new NROM(_prgRom, _chrRom, _nametableMirroring);
                    break;
                case 1:
                    MemoryMapper = new MMC1(_prgRomBanks, _chrRom, _prgRom, UsesCHRRAM, usePrgRam, prgRAMSize, batteryBacked, _nametableMirroring);
                    break;
                case 2:
                    MemoryMapper = new UxROM(_prgRom, _prgRomBanks, _chrRom, _nametableMirroring);
                    break;
                case 3:
                    MemoryMapper = new CNROM(_prgRom, _prgRomBanks, _chrRom, _nametableMirroring);
                    break;
                case 4:
                    MemoryMapper = new MMC3(_prgRom, _chrRom, UsesCHRRAM, prgRAMSize, batteryBacked, _nametableMirroring, mapperNumber);
                    break;
                case 5:
                {
                    if (prgRAMSize == 0)
                    {
                        prgRAMSize = 65536;
                        _prgRam = new byte[prgRAMSize];
                    }
                    MemoryMapper = new MMC5(_prgRom, _chrRom, UsesCHRRAM, prgRAMSize, batteryBacked, _nametableMirroring);
                    break;
                }
                case 7:
                    MemoryMapper = new AxROM(_prgRom, _chrRom);
                    break;
                case 9:
                    MemoryMapper = new MMC2(_prgRom, _chrRom, prgRAMSize, false, _nametableMirroring);
                    break;
                case 10:
                    MemoryMapper = new MMC2(_prgRom, _chrRom, prgRAMSize, true, _nametableMirroring);
                    break;
                case 11:
                case 66:
                case 148:
                case 140:
                    MemoryMapper = new GxROM(_prgRom, _chrRom, mapperNumber, _nametableMirroring);
                    break;
                case 16:
                case 153:
                case 159:
                    MemoryMapper = new BandaiFcg(
                        _prgRom,
                        _chrRom,
                        UsesCHRRAM,
                        prgRAMSize,
                        batteryBacked,
                        _nametableMirroring,
                        mapperNumber,
                        subMapperNumber);
                    break;
                case 19:
                    MemoryMapper = new Namco163(
                        _prgRom,
                        _chrRom,
                        UsesCHRRAM,
                        prgRAMSize,
                        batteryBacked,
                        subMapperNumber,
                        _nametableMirroring);
                    break;
                case 21:
                case 22:
                case 23:
                case 25:
                    MemoryMapper = new VRC4(
                        _prgRom,
                        _chrRom,
                        UsesCHRRAM,
                        prgRAMSize,
                        mapperNumber,
                        subMapperNumber,
                        _nametableMirroring);
                    break;
                case 30:
                    MemoryMapper = new Unrom512(
                        _prgRom,
                        _chrRom,
                        batteryBacked,
                        Flags6.IsFlagSet(Byte6Flags.FourScreenVRAM),
                        _nametableMirroring);
                    break;
                case 34:
                    MemoryMapper = new BNROM(_prgRom, _chrRom, prgRAMSize, _nametableMirroring);
                    break;
                case 71:
                    MemoryMapper = new UxROM(_prgRom, _prgRomBanks, _chrRom, _nametableMirroring);
                    break;
                case 24:
                case 26:
                    MemoryMapper = new VRC6(_prgRom, _chrRom, UsesCHRRAM, prgRAMSize, batteryBacked, mapperNumber, _nametableMirroring);
                    break;
                case 76:
                case 88:
                case 95:
                case 119:
                case 154:
                case 206:
                    MemoryMapper = new MMC3(_prgRom, _chrRom, UsesCHRRAM, prgRAMSize, batteryBacked, _nametableMirroring, mapperNumber);
                    break;
                case 69:
                    MemoryMapper = new SunsoftFme7(_prgRom, _chrRom, UsesCHRRAM, prgRAMSize, batteryBacked, _nametableMirroring);
                    break;
                case 85:
                    MemoryMapper = new VRC7(
                        _prgRom,
                        _chrRom,
                        UsesCHRRAM,
                        prgRAMSize,
                        batteryBacked,
                        subMapperNumber,
                        _nametableMirroring);
                    break;
                case 210:
                    MemoryMapper = new Namco175(_prgRom, _chrRom, UsesCHRRAM, prgRAMSize, batteryBacked, subMapperNumber, _nametableMirroring);
                    break;
                case 228:
                    MemoryMapper = new Action52(_prgRom, _chrRom);
                    break;
                default:
                    throw new Exception($"Unsupported Mapper: {mapperNumber}");
            }

            return true;
        }
    }
}
