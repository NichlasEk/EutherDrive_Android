using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace EutherDrive.Core.MdTracerCore
{
    internal class md_cartridge
    {
        public byte[] g_file = Array.Empty<byte>(); // ROM File data
        public int g_file_size;                      // ROM File data size
        public string g_file_path = string.Empty;
        public string g_system_type = string.Empty;
        public string g_system_type_raw = string.Empty;
        public bool g_system_type_sega;
        public bool g_system_type_known;
        public string g_copyright = string.Empty;
        public string g_game_title1 = string.Empty;
        public string g_game_title2 = string.Empty;
        public string g_serial_number = string.Empty;
        public string g_device_support = string.Empty;
        public string g_device_support_raw = string.Empty;
        public uint g_rom_start;
        public uint g_rom_end;
        public uint g_ram_start;
        public uint g_ram_end;
        public byte g_extra_memory_type;
        public byte g_extra_memory_flags;
        public bool g_extra_memory_ra;
        public bool g_extra_memory_is_sram;
        public bool g_extra_memory_is_eeprom;
        public bool g_extra_memory_battery;
        public string g_extra_memory_access = string.Empty;
        public uint g_extra_memory_start;
        public uint g_extra_memory_end;
        public string g_country = string.Empty;
        public string g_region_raw = string.Empty;
        public bool g_region_is_new_style;
        public byte g_region_mask;
        public bool g_region_supports_japan;
        public bool g_region_supports_usa;
        public bool g_region_supports_europe;
        public ushort g_rom_checksum_header;
        public ushort g_rom_checksum_calc;
        public bool g_rom_checksum_match;
        public string g_modem_support = string.Empty;
        public int g_smd_header_size;
        public bool g_smd_deinterleaved;

        public bool load(string in_romname)
        {
            try
            {
                // Läs råfilen
                g_file_path = in_romname;
                g_file = File.ReadAllBytes(in_romname);
                g_file_size = g_file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                Debug.WriteLine($"[CARTRIDGE] Load failed: {ex.Message}");
                return false;
            }

            // ZIP? (PK\x03\x04)
            if (g_file_size >= 4 &&
                g_file[0] == 0x50 && g_file[1] == 0x4B && g_file[2] == 0x03 && g_file[3] == 0x04)
            {
                try
                {
                    using var fs = File.OpenRead(in_romname);
                    using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

                    // Välj lämplig post (helst .bin/.md, annars största filen)
                    ZipArchiveEntry? pick = null;
                    foreach (var e in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Name)) continue; // directory
                        var name = e.Name.ToLowerInvariant();
                        if (name.EndsWith(".bin") || name.EndsWith(".md"))
                        {
                            pick = e;
                            break;
                        }
                        if (pick == null || e.Length > pick.Length) pick = e;
                    }

                    if (pick == null)
                    {
                        Debug.WriteLine("[CARTRIDGE] ZIP contained no files.");
                        return false;
                    }

                    using var ms = new MemoryStream();
                    using (var zs = pick.Open()) zs.CopyTo(ms);
                    g_file = ms.ToArray();
                    g_file_size = g_file.Length;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CARTRIDGE] ZIP read failed: {ex.Message}");
                    return false;
                }
            }

            var normalized = md_rom_utils.NormalizeMegaDriveRom(g_file);
            g_file = normalized.Data;
            g_file_size = g_file.Length;
            g_smd_header_size = normalized.HeaderSize;
            g_smd_deinterleaved = normalized.Deinterleaved;

            // Minimistorlek för att rymma headern vi läser (0x1F2)
            if (g_file_size < 0x1F3)
            {
                Debug.WriteLine($"[CARTRIDGE] File too small: {g_file_size} bytes.");
                return false;
            }

            // Läs headerfält
            g_system_type_raw   = get_string_raw(0x100, 16);
            g_system_type       = g_system_type_raw.TrimEnd();
            g_system_type_sega  = g_system_type_raw.StartsWith("SEGA", StringComparison.OrdinalIgnoreCase);
            g_system_type_known = IsKnownSystemType(g_system_type);
            if (!g_system_type_sega)
                Debug.WriteLine($"[CARTRIDGE] System type missing SEGA: '{g_system_type_raw}'.");

            g_copyright         = get_string(0x110, 0x11F);
            g_game_title1       = get_string(0x120, 0x14F);
            g_game_title2       = get_string(0x150, 0x17F);
            g_serial_number     = get_string(0x180, 0x18D);
            g_rom_checksum_header = get_ushort(0x18E);
            g_device_support_raw  = get_string_raw(0x190, 16);
            g_device_support      = g_device_support_raw.TrimEnd();
            g_rom_start         = get_uint(0x1A0);
            g_rom_end           = get_uint(0x1A4);
            g_ram_start         = get_uint(0x1A8);
            g_ram_end           = get_uint(0x1AC);
            g_extra_memory_ra   = IsExtraMemorySignaturePresent();
            g_extra_memory_type = get_byte(0x1B2);
            g_extra_memory_flags= get_byte(0x1B3);
            g_extra_memory_start= get_uint(0x1B4);
            g_extra_memory_end  = get_uint(0x1B8);
            g_modem_support     = get_string_raw(0x1BC, 12).TrimEnd();
            g_region_raw        = get_string_raw(0x1F0, 3);
            g_country           = g_region_raw.TrimEnd();

            ParseExtraMemory();
            ParseRegionSupport();
            g_rom_checksum_calc = ComputeRomChecksum();
            g_rom_checksum_match = g_rom_checksum_calc == g_rom_checksum_header;

            return true;
        }

        public string get_string(int in_start, int in_end)
        {
            // Bounds-säkring
            if (in_start < 0) in_start = 0;
            if (in_end >= g_file_size) in_end = g_file_size - 1;
            if (in_end < in_start || g_file_size == 0) return string.Empty;

            var len = (in_end - in_start + 1);
            var s = System.Text.Encoding.ASCII.GetString(g_file, in_start, len);
            return s.TrimEnd('\0', ' ', '\t', '\r', '\n');
        }

        public string get_string_raw(int in_start, int length)
        {
            if (length <= 0 || g_file_size == 0)
                return string.Empty;
            if (in_start < 0)
                in_start = 0;
            if (in_start >= g_file_size)
                return string.Empty;
            int len = Math.Min(length, g_file_size - in_start);
            var s = System.Text.Encoding.ASCII.GetString(g_file, in_start, len);
            return s.Replace('\0', ' ');
        }

        public uint get_uint(int in_start)
        {
            // Big-endian 32-bit
            if (in_start < 0 || in_start + 3 >= g_file_size) return 0;
            uint w_val = 0;
            for (int i = 0; i < 4; i++)
                w_val = (w_val << 8) + g_file[in_start + i];
            return w_val;
        }

        public ushort get_ushort(int in_start)
        {
            if (in_start < 0 || in_start + 1 >= g_file_size) return 0;
            return (ushort)((g_file[in_start] << 8) | g_file[in_start + 1]);
        }

        public byte get_byte(int in_start)
        {
            if (in_start < 0 || in_start >= g_file_size) return 0;
            return g_file[in_start];
        }

        private bool IsExtraMemorySignaturePresent()
        {
            return g_file_size >= 0x1B2 &&
                   g_file[0x1B0] == (byte)'R' &&
                   g_file[0x1B1] == (byte)'A';
        }

        private void ParseExtraMemory()
        {
            g_extra_memory_is_sram = false;
            g_extra_memory_is_eeprom = false;
            g_extra_memory_battery = false;
            g_extra_memory_access = string.Empty;

            if (!g_extra_memory_ra)
                return;

            byte type = g_extra_memory_type;
            byte flags = g_extra_memory_flags;

            if (type == 0xE8 && flags == 0x40)
            {
                g_extra_memory_is_eeprom = true;
                g_extra_memory_battery = true;
                g_extra_memory_access = "eeprom";
                return;
            }

            switch (type)
            {
                case 0xA0:
                    g_extra_memory_is_sram = true;
                    g_extra_memory_access = "word";
                    g_extra_memory_battery = false;
                    break;
                case 0xB0:
                    g_extra_memory_is_sram = true;
                    g_extra_memory_access = "byte-even";
                    g_extra_memory_battery = false;
                    break;
                case 0xB8:
                    g_extra_memory_is_sram = true;
                    g_extra_memory_access = "byte-odd";
                    g_extra_memory_battery = false;
                    break;
                case 0xE0:
                    g_extra_memory_is_sram = true;
                    g_extra_memory_access = "word";
                    g_extra_memory_battery = true;
                    break;
                case 0xF0:
                    g_extra_memory_is_sram = true;
                    g_extra_memory_access = "byte-even";
                    g_extra_memory_battery = true;
                    break;
                case 0xF8:
                    g_extra_memory_is_sram = true;
                    g_extra_memory_access = "byte-odd";
                    g_extra_memory_battery = true;
                    break;
            }
        }

        private void ParseRegionSupport()
        {
            g_region_is_new_style = false;
            g_region_mask = 0;
            g_region_supports_japan = false;
            g_region_supports_usa = false;
            g_region_supports_europe = false;

            string raw = g_region_raw;
            if (string.IsNullOrWhiteSpace(raw))
                return;

            char c0 = raw[0];
            bool restBlank = raw.Length <= 1 || raw.AsSpan(1).ToString().Trim() == string.Empty;
            byte mask;
            bool parsed = TryParseHexNibble(c0, out mask);
            if (!parsed)
                mask = 0;
            bool singleHex = raw.Length >= 1 && raw.Length <= 3 &&
                             restBlank &&
                             c0 != 'E' &&
                             parsed;

            if (singleHex)
            {
                g_region_is_new_style = true;
                g_region_mask = mask;
                g_region_supports_japan = (mask & 0x1) != 0;
                g_region_supports_usa = (mask & 0x4) != 0;
                g_region_supports_europe = (mask & 0x8) != 0;
                return;
            }

            for (int i = 0; i < raw.Length && i < 3; i++)
            {
                switch (char.ToUpperInvariant(raw[i]))
                {
                    case 'J':
                        g_region_supports_japan = true;
                        break;
                    case 'U':
                        g_region_supports_usa = true;
                        break;
                    case 'E':
                        g_region_supports_europe = true;
                        break;
                }
            }
        }

        private static bool TryParseHexNibble(char c, out byte value)
        {
            value = 0;
            if (c >= '0' && c <= '9')
            {
                value = (byte)(c - '0');
                return true;
            }
            if (c >= 'A' && c <= 'F')
            {
                value = (byte)(10 + (c - 'A'));
                return true;
            }
            if (c >= 'a' && c <= 'f')
            {
                value = (byte)(10 + (c - 'a'));
                return true;
            }
            return false;
        }

        private ushort ComputeRomChecksum()
        {
            if (g_file_size < 0x200)
                return 0;
            uint sum = 0;
            for (int i = 0x200; i < g_file_size; i += 2)
            {
                byte hi = g_file[i];
                byte lo = (i + 1) < g_file_size ? g_file[i + 1] : (byte)0x00;
                sum += (uint)((hi << 8) | lo);
            }
            return (ushort)(sum & 0xFFFF);
        }

        private static bool IsKnownSystemType(string systemType)
        {
            return systemType == "SEGA MEGA DRIVE" ||
                   systemType == "SEGA GENESIS" ||
                   systemType == "SEGA 32X" ||
                   systemType == "SEGA EVERDRIVE" ||
                   systemType == "SEGA SSF" ||
                   systemType == "SEGA MEGAWIFI" ||
                   systemType == "SEGA PICO" ||
                   systemType == "SEGA TERA68K" ||
                   systemType == "SEGA TERA286";
        }
    }
}
