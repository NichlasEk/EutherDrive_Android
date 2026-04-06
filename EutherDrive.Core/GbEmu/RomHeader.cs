using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EutherDrive.Core.GbEmu
{
    public class RomHeader
    {
        public required string Title { get; set; }
        public required string ManufacturerCode { get; set; }
        public string LicenseeCode { get; private set; }
        public byte CartridgeType { get; private set; }
        public int RomSize { get; private set; }
        public int RamSize { get; private set; }
        public string DestinationCode { get; private set; }
        public string CgbFlag { get; private set; }
        public byte Version { get; private set; }
        public byte HeaderChecksum { get; private set; }
        public ushort GlobalChecksum { get; private set; }

        private byte[] _romData; // keep raw bytes for checksum verification

        private static readonly Dictionary<byte, string> CartridgeTypes = new()
        {
            { 0x00, "ROM ONLY" }, { 0x01, "MBC1" }, { 0x02, "MBC1 + RAM" }, { 0x03, "MBC1 + RAM + Battery" },
            { 0x05, "MBC2" }, { 0x06, "MBC2 + Battery" }, { 0x08, "ROM + RAM" }, { 0x09, "ROM + RAM + Battery" },
            { 0x0B, "MMM01" }, { 0x0C, "MMM01 + RAM" }, { 0x0D, "MMM01 + RAM + Battery" },
            { 0x0F, "MBC3 + Timer + Battery" }, { 0x10, "MBC3 + Timer + RAM + Battery" },
            { 0x11, "MBC3" }, { 0x12, "MBC3 + RAM" }, { 0x13, "MBC3 + RAM + Battery" },
            { 0x19, "MBC5" }, { 0x1A, "MBC5 + RAM" }, { 0x1B, "MBC5 + RAM + Battery" },
            { 0x1C, "MBC5 + Rumble" }, { 0x1D, "MBC5 + Rumble + RAM" }, { 0x1E, "MBC5 + Rumble + RAM + Battery" },
            { 0x20, "MBC6" }, { 0x22, "MBC7 + Sensor + Rumble + RAM + Battery" },
            { 0xFC, "Pocket Camera" }, { 0xFD, "Bandai TAMA5" },
            { 0xFE, "HuC3" }, { 0xFF, "HuC1 + RAM + Battery" }
        };

        private static readonly Dictionary<string, string> LicenseeCodes = new()
        {
            { "00", "None" }, { "01", "Nintendo R&D1" }, { "08", "Capcom" }, { "13", "Electronic Arts" },
            { "18", "Hudson Soft" }, { "19", "b-ai" }, { "20", "kss" }, { "22", "pow" },
            { "24", "PCM Complete" }, { "25", "San-X" }, { "28", "Kemco Japan" }, { "29", "Seta" },
            { "30", "Viacom" }, { "31", "Nintendo" }, { "32", "Bandai" }, { "34", "Konami" },
            { "35", "Hector" }, { "37", "Taito" }, { "38", "Hudson" }, { "39", "Banpresto" },
            { "41", "Ubi Soft" }, { "42", "Atlus" }, { "44", "Malibu" }, { "46", "Angel" },
            { "47", "Bullet-Proof" }, { "49", "IREM" }, { "50", "Absolute" }, { "51", "Acclaim" },
            { "52", "Activision" }, { "53", "American Sammy" }, { "54", "Konami" }, { "55", "Hi Tech" },
            { "56", "LJN" }, { "57", "Matchbox" }, { "58", "Mattel" }, { "59", "Milton Bradley" },
            { "60", "Titus" }, { "61", "Virgin" }, { "64", "LucasArts" }, { "67", "Ocean" },
            { "69", "Electronic Arts" }, { "70", "Infogrames" }, { "71", "Interplay" },
            { "72", "Broderbund" }, { "73", "Sculptured" }, { "75", "SCI" }, { "78", "THQ" },
            { "79", "Accolade" }, { "80", "Misawa" }, { "83", "lozc" }, { "86", "Tokuma Shoten" },
            { "87", "Tsukuda Original" }, { "91", "Chunsoft" }, { "92", "Video System" },
            { "93", "Ocean/Acclaim" }, { "95", "Varie" }, { "96", "Yonezawa/s’pal" },
            { "97", "Kaneko" }, { "99", "Pack-In-Soft" }, { "A4", "Konami (Yu-Gi-Oh!)" }
        };

        public static RomHeader Parse(byte[] romData)
        {
            if (romData.Length < 0x150)
                throw new ArgumentException("ROM file too small to contain a valid Game Boy header.");

            string LicCode;
            if (romData[0x014B] == 0x33)
            {
                // Licensee code (new 0x0144–0x0145)
                LicCode = Encoding.ASCII.GetString(romData, 0x0144, 2);
            }
            else
            {
                LicCode = romData[0x014B].ToString("X2");
            }

            var header = new RomHeader
            {
                _romData = romData,

                // Title (0x0134–0x0143)
                Title = Encoding.ASCII.GetString(romData, 0x0134, 16).TrimEnd('\0', (char)0x80),

                // Manufacturer Code (0x013F–0x0142)
                ManufacturerCode = Encoding.ASCII.GetString(romData, 0x013F, 4).Trim(),

                LicenseeCode = LicCode,

                // Cartridge type (0x0147)
                CartridgeType = romData[0x0147],

                // ROM size (0x0148)
                RomSize = 32 * 1024 << romData[0x0148],

                // RAM size (0x0149)
                RamSize = romData[0x0149] switch
                {
                    0x00 => 0,
                    0x01 => 2 * 1024,
                    0x02 => 8 * 1024,
                    0x03 => 32 * 1024,
                    0x04 => 128 * 1024,
                    0x05 => 64 * 1024,
                    _ => 0
                },

                // Destination code (0x014A)
                DestinationCode = romData[0x014A] == 0x00 ? "Japanese" : "Non-Japanese",

                // CGB Flag (0x0143)
                CgbFlag = romData[0x0143] switch
                {
                    0x80 => "CGB Compatible",
                    0xC0 => "CGB Only",
                    _ => "DMG Only"
                },

                // Version (0x014C)
                Version = romData[0x014C],

                // Header checksum (0x014D)
                HeaderChecksum = romData[0x014D],

                // Global checksum (0x014E–0x014F)
                GlobalChecksum = BitConverter.ToUInt16([romData[0x014F], romData[0x014E]], 0)
            };

            return header;
        }

        /// <summary>
        /// Verifies the header checksum (0x0134–0x014C).
        /// </summary>
        public bool VerifyHeaderChecksum()
        {
            int sum = 0;
            for (int i = 0x0134; i <= 0x014C; i++)
                sum = (sum - _romData[i] - 1) & 0xFF;

            return sum == HeaderChecksum;
        }

        /// <summary>
        /// Verifies the global checksum (entire ROM, except bytes 0x014E–0x014F).
        /// </summary>
        public bool VerifyGlobalChecksum()
        {
            int sum = 0;
            for (int i = 0; i < _romData.Length; i++)
            {
                if (i == 0x014E || i == 0x014F) continue;
                sum = (sum + _romData[i]) & 0xFFFF;
            }
            return sum == GlobalChecksum;
        }

        private string CartridgeTypeName =>
            CartridgeTypes.ContainsKey(CartridgeType) ? CartridgeTypes[CartridgeType] : $"Unknown (0x{CartridgeType:X2})";

        private string LicenseeName =>
            LicenseeCodes.ContainsKey(LicenseeCode) ? LicenseeCodes[LicenseeCode] : $"Unknown ({LicenseeCode})";

        public override string ToString()
        {
            return $"Title: {Title}\n" +
                   $"Manufacturer: {ManufacturerCode}\n" +
                   $"Licensee: {LicenseeName}\n" +
                   $"Cartridge Type: {CartridgeTypeName}\n" +
                   $"ROM Size: {RomSize / 1024} KB\n" +
                   $"RAM Size: {RamSize / 1024} KB\n" +
                   $"Region: {DestinationCode}\n" +
                   $"CGB Flag: {CgbFlag}\n" +
                   $"Version: {Version}\n" +
                   $"Header Checksum: 0x{HeaderChecksum:X2} ({(VerifyHeaderChecksum() ? "OK" : "FAIL")})\n" +
                   $"Global Checksum: 0x{GlobalChecksum:X4} ({(VerifyGlobalChecksum() ? "OK" : "FAIL")})";
        }
    }
}
