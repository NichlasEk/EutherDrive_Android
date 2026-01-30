using System;
using EutherDrive.Core.MdTracerCore;

class TestAndiFix
{
    static void Main()
    {
        Console.WriteLine("Testing ANDI.W fix...");
        
        // Initiera
        md_m68k.g_reg_data = new md_m68k.Reg32[8];
        md_m68k.g_reg_addr = new md_m68k.Reg32[8];
        for (int i = 0; i < 8; i++)
        {
            md_m68k.g_reg_data[i] = new md_m68k.Reg32();
            md_m68k.g_reg_addr[i] = new md_m68k.Reg32();
        }
        
        // Test 1: write_g_reg_data word operation
        Console.WriteLine("\nTest 1: write_g_reg_data word operation");
        md_m68k.g_reg_data[0].l = 0x07000000;
        md_m68k.write_g_reg_data(0, 1, 0x00FF); // word operation
        Console.WriteLine($"D0 after write_g_reg_data(0, 1, 0x00FF): 0x{md_m68k.g_reg_data[0].l:X8}");
        Console.WriteLine($"Expected: 0x070000FF (high 16 bits preserved)");
        
        // Test 2: adressing_func_write for data register
        Console.WriteLine("\nTest 2: adressing_func_write for data register");
        md_m68k.g_reg_data[0].l = 0x07000000;
        var cpu = new md_m68k();
        cpu.adressing_func_write(0, 0, 1, 0x00FF); // mode 0 = data register
        Console.WriteLine($"D0 after adressing_func_write(0, 0, 1, 0x00FF): 0x{md_m68k.g_reg_data[0].l:X8}");
        Console.WriteLine($"Expected: 0x070000FF (high 16 bits preserved)");
        
        // Test 3: Simulate rol.l #8,d0 -> andi.w #$00FF,d0 -> asl.w #5,d0
        Console.WriteLine("\nTest 3: Simulate Madou sequence");
        md_m68k.g_reg_data[0].l = 0x00070000;
        Console.WriteLine($"Start: D0 = 0x{md_m68k.g_reg_data[0].l:X8}");
        
        // rol.l #8,d0: 0x00070000 -> 0x07000000
        uint val = md_m68k.g_reg_data[0].l;
        val = (val << 8) | (val >> 24);
        md_m68k.g_reg_data[0].l = val;
        Console.WriteLine($"After rol.l #8,d0: D0 = 0x{md_m68k.g_reg_data[0].l:X8}");
        
        // andi.w #$00FF,d0: should give 0x07000000 (high 16 bits preserved)
        md_m68k.write_g_reg_data(0, 1, 0x00FF);
        Console.WriteLine($"After andi.w #$00FF,d0: D0 = 0x{md_m68k.g_reg_data[0].l:X8}");
        Console.WriteLine($"Expected: 0x07000000 (high 16 bits preserved)");
        
        // asl.w #5,d0: shift low 16 bits by 5
        // 0x07000000 -> low 16 bits: 0x0000, shift left 5: 0x0000
        // Result should still be 0x07000000
        val = md_m68k.g_reg_data[0].l;
        uint low16 = val & 0x0000FFFF;
        low16 = (low16 << 5) & 0x0000FFFF;
        val = (val & 0xFFFF0000) | low16;
        md_m68k.g_reg_data[0].l = val;
        Console.WriteLine($"After asl.w #5,d0: D0 = 0x{md_m68k.g_reg_data[0].l:X8}");
        Console.WriteLine($"Expected: 0x07000000");
        
        // Final index should be (D0 >> 16) & 0xFF = 0x07
        uint index = (md_m68k.g_reg_data[0].l >> 16) & 0xFF;
        Console.WriteLine($"Index = (D0 >> 16) & 0xFF = 0x{index:X2}");
        Console.WriteLine($"Expected: 0x07");
        
        if (index == 0x07)
            Console.WriteLine("\nSUCCESS: Fix works correctly!");
        else
            Console.WriteLine($"\nFAILURE: Index is 0x{index:X2}, should be 0x07");
    }
}