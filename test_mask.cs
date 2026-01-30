using System;
using EutherDrive.Core.MdTracerCore;

class Program
{
    static void Main()
    {
        Console.WriteLine("Running register mask test...");
        
        // Initiera statiska fält
        md_m68k.g_reg_data = new md_m68k.Reg32[8];
        md_m68k.g_reg_addr = new md_m68k.Reg32[8];
        for (int i = 0; i < 8; i++)
        {
            md_m68k.g_reg_data[i] = new md_m68k.Reg32();
            md_m68k.g_reg_addr[i] = new md_m68k.Reg32();
        }
        
        try
        {
            md_m68k.RunRegisterMaskTest();
            Console.WriteLine("Mask test PASSED!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Mask test FAILED: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}