using System;
using EutherDrive.Core.MdTracerCore;

class Program
{
    static void Main()
    {
        Console.WriteLine("Testing Z80 execution...");
        
        // Create Z80 instance
        var z80 = new md_z80();
        z80.initialize();
        
        // Check initial state
        Console.WriteLine($"Initial PC: 0x{z80.DebugPc:X4}");
        Console.WriteLine($"Initial SP: 0x{z80.DebugSp:X4}");
        
        // Run a few instructions
        for (int i = 0; i < 10; i++)
        {
            z80.run(4); // Run 4 cycles
            Console.WriteLine($"Step {i}: PC=0x{z80.DebugPc:X4}");
        }
    }
}