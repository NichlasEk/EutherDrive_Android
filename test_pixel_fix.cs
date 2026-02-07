using System;
using System.IO;

class TestPixelFix
{
    static void Main()
    {
        Console.WriteLine("Testing pixel extraction fix...");
        Console.WriteLine();
        Console.WriteLine("Current code: (word >> ((3 - (x & 3)) << 2)) & 0x0f");
        Console.WriteLine("Assumes big-endian: pixel 0 in bits 12-15");
        Console.WriteLine();
        Console.WriteLine("Test with word = 0x1234 (binary: 0001 0010 0011 0100)");
        Console.WriteLine("Big-endian interpretation:");
        Console.WriteLine("  Bits 12-15: 0001 = pixel 0 = 1");
        Console.WriteLine("  Bits 8-11:  0010 = pixel 1 = 2");
        Console.WriteLine("  Bits 4-7:   0011 = pixel 2 = 3");
        Console.WriteLine("  Bits 0-3:   0100 = pixel 3 = 4");
        Console.WriteLine("  Pixels: [1,2,3,4]");
        Console.WriteLine();
        Console.WriteLine("Little-endian interpretation:");
        Console.WriteLine("  Bits 0-3:   0100 = pixel 0 = 4");
        Console.WriteLine("  Bits 4-7:   0011 = pixel 1 = 3");
        Console.WriteLine("  Bits 8-11:  0010 = pixel 2 = 2");
        Console.WriteLine("  Bits 12-15: 0001 = pixel 3 = 1");
        Console.WriteLine("  Pixels: [4,3,2,1]");
        Console.WriteLine();
        Console.WriteLine("If graphics look 'rotated', maybe pixel order is wrong!");
        Console.WriteLine("Changing to (word >> ((x & 3) << 2)) & 0x0f would fix it.");
        Console.WriteLine();
        Console.WriteLine("Also testing H/V flip bit swap...");
        Console.WriteLine("If H and V flip bits are swapped, graphics would look mirrored diagonally.");
    }
}