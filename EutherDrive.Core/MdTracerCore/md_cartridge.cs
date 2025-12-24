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
        public string g_system_type = string.Empty;
        public string g_copyright = string.Empty;
        public string g_game_title1 = string.Empty;
        public string g_game_title2 = string.Empty;
        public string g_serial_number = string.Empty;
        public string g_device_support = string.Empty;
        public uint g_rom_start;
        public uint g_rom_end;
        public uint g_ram_start;
        public uint g_ram_end;
        public byte g_extra_memory_type;
        public uint g_extra_memory_start;
        public uint g_extra_memory_end;
        public string g_country = string.Empty;
        public int g_smd_header_size;
        public bool g_smd_deinterleaved;

        public bool load(string in_romname)
        {
            try
            {
                // Läs råfilen
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
            g_system_type       = get_string(0x100, 0x10F).Trim();
            if (g_system_type != "SEGA MEGA DRIVE" &&
                g_system_type != "SEGA GENESIS")
            {
                Debug.WriteLine($"[CARTRIDGE] Unsupported system type: '{g_system_type}'.");
                return false;
            }

            g_copyright         = get_string(0x110, 0x11F);
            g_game_title1       = get_string(0x120, 0x14F);
            g_game_title2       = get_string(0x150, 0x17F);
            g_serial_number     = get_string(0x180, 0x18D);
            g_device_support    = get_string(0x190, 0x19F);
            g_rom_start         = get_uint(0x1A0);
            g_rom_end           = get_uint(0x1A4);
            g_ram_start         = get_uint(0x1A8);
            g_ram_end           = get_uint(0x1AC);
            g_extra_memory_type = get_byte(0x1B2);
            g_extra_memory_start= get_uint(0x1B4);
            g_extra_memory_end  = get_uint(0x1B8);
            g_country           = get_string(0x1F0, 0x1F2);

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

        public uint get_uint(int in_start)
        {
            // Big-endian 32-bit
            if (in_start < 0 || in_start + 3 >= g_file_size) return 0;
            uint w_val = 0;
            for (int i = 0; i < 4; i++)
                w_val = (w_val << 8) + g_file[in_start + i];
            return w_val;
        }

        public byte get_byte(int in_start)
        {
            if (in_start < 0 || in_start >= g_file_size) return 0;
            return g_file[in_start];
        }
    }
}
