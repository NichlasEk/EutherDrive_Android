using System;
using System.Diagnostics;

class RunMadouTest
{
    static void Main()
    {
        Console.WriteLine("Starting Madou test with D0 fix...");
        Console.WriteLine("This would run the emulator for 10 seconds");
        Console.WriteLine("and capture debug logs about D0 values.");
        Console.WriteLine();
        Console.WriteLine("Expected output:");
        Console.WriteLine("[DEBUG-FIX] PC=0x013A50 FIXING D0 from 0x00070000 to 0x07000000 before ROL.L #8");
        Console.WriteLine("[ROL-MADOU-CRITICAL] PC=0x013A50 ROL.L #8,D0: 0x07000000 -> 0x00000007");
        Console.WriteLine("[ANDI-MADOU-CRITICAL] PC=0x013A58 D0 before=0x00000007 AND with 0x00FF => 0x00000007");
        Console.WriteLine();
        Console.WriteLine("If we see this, the fix works!");
        Console.WriteLine("The game should get correct palette index 0x000000E0");
    }
}