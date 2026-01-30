using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore;

internal partial class md_m68k
{
    // Regression test for word/byte masking when writing to Dn/An.
    // Call manually from a debug entry point if needed.
    internal static void RunRegisterMaskTest()
    {
        // Data register masking via write_g_reg_data
        g_reg_data[0].l = 0x07000000;
        write_g_reg_data(0, 1, 0x00FF);
        Console.WriteLine($"[MASK-TEST] write_g_reg_data word: 0x{g_reg_data[0].l:X8} (expected 0x070000FF)");
        Debug.Assert(g_reg_data[0].l == 0x070000FF, "write_g_reg_data word should keep high 16 bits.");

        g_reg_data[0].l = 0x12345678;
        write_g_reg_data(0, 0, 0xAA);
        Console.WriteLine($"[MASK-TEST] write_g_reg_data byte: 0x{g_reg_data[0].l:X8} (expected 0x123456AA)");
        Debug.Assert(g_reg_data[0].l == 0x123456AA, "write_g_reg_data byte should keep high 24 bits.");

        g_reg_data[0].l = 0x00000000;
        write_g_reg_data(0, 2, 0xAABBCCDD);
        Debug.Assert(g_reg_data[0].l == 0xAABBCCDD, "write_g_reg_data long should overwrite all bits.");

        // Addressing write for Dn/An (addressing_func_write)
        var cpu = new md_m68k();

        g_reg_data[1].l = 0x07000000;
        cpu.adressing_func_write(0, 1, 1, 0x00FF);
        Console.WriteLine($"[MASK-TEST] adressing_func_write Dn word: 0x{g_reg_data[1].l:X8} (expected 0x070000FF)");
        Debug.Assert(g_reg_data[1].l == 0x070000FF, "adressing_func_write Dn word should keep high 16 bits.");

        g_reg_addr[1].l = 0x07000000;
        cpu.adressing_func_write(1, 1, 1, 0x00FF);
        Console.WriteLine($"[MASK-TEST] adressing_func_write An word: 0x{g_reg_addr[1].l:X8} (expected 0x070000FF)");
        Debug.Assert(g_reg_addr[1].l == 0x070000FF, "adressing_func_write An word should keep high 16 bits.");

        g_reg_addr[1].l = 0x12345678;
        cpu.adressing_func_write(1, 1, 0, 0xAA);
        Debug.Assert(g_reg_addr[1].l == 0x123456AA, "adressing_func_write An byte should keep high 24 bits.");
    }
}
