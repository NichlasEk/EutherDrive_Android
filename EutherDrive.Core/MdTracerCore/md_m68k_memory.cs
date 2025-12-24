namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        // Låt den börja som null så vi ser tydligt att init behövs.
        public static byte[]? g_memory;

        private const int MemorySize = 0x1000000; // 16 MiB

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

        private static uint NormalizeAddr(uint in_address)
        {
            in_address &= 0x00FF_FFFF;
            if (in_address >= 0x00E0_0000)
                in_address = (in_address & 0x0000_FFFF) | 0x00FF_0000;
            return in_address;
        }

        //----------------------------------------------------------------
        // read
        //----------------------------------------------------------------
        public static byte read8(uint in_address)
        {
            InitMemoryIfNeeded();
            var mem = g_memory!;

            var addr = NormalizeAddr(in_address);
            byte value = mem[addr];
            RecordMemoryAccess(addr, 1, false, value);
            return value;
        }

        public static ushort read16(uint in_address)
        {
            InitMemoryIfNeeded();
            var mem = g_memory!;

            var addr = NormalizeAddr(in_address);

            byte hi = mem[addr];
            byte lo = mem[addr + 1];
            ushort value = (ushort)((hi << 8) | lo);
            RecordMemoryAccess(addr, 2, false, value);
            return value;
        }

        public static uint read32(uint in_address)
        {
            InitMemoryIfNeeded();
            var mem = g_memory!;

            var addr = NormalizeAddr(in_address);

            uint b3 = mem[addr];
            uint b2 = mem[addr + 1];
            uint b1 = mem[addr + 2];
            uint b0 = mem[addr + 3];

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

            var addr = NormalizeAddr(in_address);
            mem[addr] = in_data;
            RecordMemoryAccess(addr, 1, true, in_data);
        }

        public static void write16(uint in_address, ushort in_data)
        {
            InitMemoryIfNeeded();
            var mem = g_memory!;

            var addr = NormalizeAddr(in_address);

            mem[addr]     = (byte)(in_data >> 8);
            mem[addr + 1] = (byte)(in_data & 0x00FF);
            RecordMemoryAccess(addr, 2, true, in_data);
        }

        public static void write32(uint in_address, uint in_data)
        {
            InitMemoryIfNeeded();
            var mem = g_memory!;

            var addr = NormalizeAddr(in_address);

            mem[addr]     = (byte)(in_data >> 24);
            mem[addr + 1] = (byte)((in_data >> 16) & 0x00FF);
            mem[addr + 2] = (byte)((in_data >> 8) & 0x00FF);
            mem[addr + 3] = (byte)(in_data & 0x00FF);
            RecordMemoryAccess(addr, 4, true, in_data);
        }
    }
}
