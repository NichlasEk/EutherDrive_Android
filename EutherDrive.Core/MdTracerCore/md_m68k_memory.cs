using System.Globalization;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        // Låt den börja som null så vi ser tydligt att init behövs.
        public static byte[]? g_memory;

        private const int MemorySize = 0x1000000; // 16 MiB
        private static readonly bool RomWriteProtect = ReadEnvDefaultOn("EUTHERDRIVE_ROM_WRITE_PROTECT");
        private static readonly byte? ForceB154ReadValue = ParseByteEnv("EUTHERDRIVE_FORCE_B154_READ");
        private static readonly int ForceB154ReadLimit =
            ParseTraceLimit("EUTHERDRIVE_FORCE_B154_READ_LIMIT", 8);
        private static int _forceB154ReadRemaining = ForceB154ReadLimit;

        /// <summary>
        /// Säkerställ att RAM/ROM-address-space är allokerad.
        /// Safe att kalla många gånger.
        /// </summary>
        public static void InitMemoryIfNeeded()
        {
            g_memory ??= new byte[MemorySize];
        }

        /// <summary>
        /// Optional: Nolla minnet snabbt (kräver att minnet finns).
        /// </summary>
        public static void ClearMemory()
        {
            InitMemoryIfNeeded();
            System.Array.Clear(g_memory!, 0, g_memory!.Length);
        }

        private static bool ReadEnvDefaultOn(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw))
                return true;
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static byte? ParseByteEnv(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            raw = raw.Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(2);
            if (byte.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte hex))
                return hex;
            if (byte.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte dec))
                return dec;
            return null;
        }

        private static int ParseTraceLimit(string name, int fallback)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            raw = raw.Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return fallback;
            if (value <= 0)
                return int.MaxValue;
            return value;
        }

        private static uint NormalizeAddr(uint in_address)
        {
            in_address &= 0x00FF_FFFF;
            if (in_address >= 0x00E0_0000)
                in_address = (in_address & 0x0000_FFFF) | 0x00FF_0000;
            return in_address;
        }

        private static uint GetRomLimit()
        {
            var cart = md_main.g_md_cartridge;
            if (cart == null)
                return 0;
            uint romEnd = cart.g_rom_end;
            if (romEnd != 0)
                return romEnd + 1;
            int size = cart.g_file_size;
            if (size <= 0)
                return 0;
            return (uint)size;
        }

        private static bool IsCartRamAddress(uint addr)
        {
            var cart = md_main.g_md_cartridge;
            if (cart == null)
                return false;
            if (cart.g_ram_end >= cart.g_ram_start && cart.g_ram_end != 0)
            {
                if (addr >= cart.g_ram_start && addr <= cart.g_ram_end)
                    return true;
            }
            if (cart.g_extra_memory_end >= cart.g_extra_memory_start && cart.g_extra_memory_end != 0)
            {
                if (addr >= cart.g_extra_memory_start && addr <= cart.g_extra_memory_end)
                    return true;
            }
            return false;
        }

        private static bool IsRomWriteBlocked(uint addr)
        {
            if (!RomWriteProtect)
                return false;
            uint limit = GetRomLimit();
            if (limit == 0 || addr >= limit)
                return false;
            return !IsCartRamAddress(addr);
        }

        //----------------------------------------------------------------
        // read
        //----------------------------------------------------------------
        public static byte read8(uint in_address)
        {
            InitMemoryIfNeeded();
            var mem = g_memory!;

            var addr = NormalizeAddr(in_address);
            var bus = md_main.g_md_bus;
            if (bus?.OverrideBus != null && bus.OverrideBus.TryRead8(addr, out byte overrideValue))
                return overrideValue;
            byte value = mem[addr];
            if (addr == 0x00FFB154 && ForceB154ReadValue.HasValue)
            {
                byte forced = ForceB154ReadValue.Value;
                if (_forceB154ReadRemaining > 0)
                {
                    System.Console.WriteLine(
                        $"[B154-OVERRIDE] pc68k=0x{g_reg_PC:X6} addr=0x{addr:X6} raw=0x{value:X2} forced=0x{forced:X2}");
                    if (_forceB154ReadRemaining != int.MaxValue)
                        _forceB154ReadRemaining--;
                }
                value = forced;
            }
            RecordMemoryAccess(addr, 1, false, value);
            return value;
        }

        public static ushort read16(uint in_address)
        {
            InitMemoryIfNeeded();
            var mem = g_memory!;

            var addr = NormalizeAddr(in_address);
            var bus = md_main.g_md_bus;
            if (bus?.OverrideBus != null && bus.OverrideBus.TryRead16(addr, out ushort overrideValue))
                return overrideValue;

            uint addr1 = NormalizeAddr(in_address + 1);
            byte hi = mem[addr];
            byte lo = mem[addr1];
            ushort value = (ushort)((hi << 8) | lo);
            RecordMemoryAccess(addr, 2, false, value);
            return value;
        }

        public static uint read32(uint in_address)
        {
            InitMemoryIfNeeded();
            var mem = g_memory!;

            var addr = NormalizeAddr(in_address);
            var bus = md_main.g_md_bus;
            if (bus?.OverrideBus != null && bus.OverrideBus.TryRead32(addr, out uint overrideValue))
                return overrideValue;

            uint addr1 = NormalizeAddr(in_address + 1);
            uint addr2 = NormalizeAddr(in_address + 2);
            uint addr3 = NormalizeAddr(in_address + 3);

            uint b3 = mem[addr];
            uint b2 = mem[addr1];
            uint b1 = mem[addr2];
            uint b0 = mem[addr3];

            uint value = (b3 << 24) | (b2 << 16) | (b1 << 8) | b0;
            RecordMemoryAccess(addr, 4, false, value);
            return value;
        }

        //----------------------------------------------------------------
        // write
        //----------------------------------------------------------------
        public static void write8(uint in_address, byte in_data)
        {
            InitMemoryIfNeeded();
            var mem = g_memory!;

            uint logical = in_address & 0x00FF_FFFF;
            if (IsRomWriteBlocked(logical))
                return;
            var addr = NormalizeAddr(in_address);
            var bus = md_main.g_md_bus;
            if (bus?.OverrideBus != null && bus.OverrideBus.TryWrite8(addr, in_data))
                return;
            mem[addr] = in_data;
            RecordMemoryAccess(addr, 1, true, in_data);
        }

        public static void write16(uint in_address, ushort in_data)
        {
            InitMemoryIfNeeded();
            var mem = g_memory!;

            uint logical = in_address & 0x00FF_FFFF;
            if (IsRomWriteBlocked(logical))
                return;
            var addr = NormalizeAddr(in_address);
            var bus = md_main.g_md_bus;
            if (bus?.OverrideBus != null && bus.OverrideBus.TryWrite16(addr, in_data))
                return;

            uint addr1 = NormalizeAddr(in_address + 1);
            mem[addr]  = (byte)(in_data >> 8);
            mem[addr1] = (byte)(in_data & 0x00FF);
            RecordMemoryAccess(addr, 2, true, in_data);
        }

        public static void write32(uint in_address, uint in_data)
        {
            InitMemoryIfNeeded();
            var mem = g_memory!;

            uint logical = in_address & 0x00FF_FFFF;
            if (IsRomWriteBlocked(logical))
                return;
            var addr = NormalizeAddr(in_address);
            var bus = md_main.g_md_bus;
            if (bus?.OverrideBus != null && bus.OverrideBus.TryWrite32(addr, in_data))
                return;

            uint addr1 = NormalizeAddr(in_address + 1);
            uint addr2 = NormalizeAddr(in_address + 2);
            uint addr3 = NormalizeAddr(in_address + 3);

            mem[addr]  = (byte)(in_data >> 24);
            mem[addr1] = (byte)((in_data >> 16) & 0x00FF);
            mem[addr2] = (byte)((in_data >> 8) & 0x00FF);
            mem[addr3] = (byte)(in_data & 0x00FF);
            RecordMemoryAccess(addr, 4, true, in_data);
        }
    }
}
