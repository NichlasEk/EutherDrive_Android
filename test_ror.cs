using System;

class Program
{
    static void Main()
    {
        uint value = 0x00070000;
        Console.WriteLine($"Start: 0x{value:X8}");
        
        // Simulate ROR.W #8 (rotate low word right by 8)
        ushort lowWord = (ushort)(value & 0xFFFF);
        ushort rotatedLow = (ushort)((lowWord >> 8) | (lowWord << 8));
        uint result1 = (value & 0xFFFF0000) | rotatedLow;
        Console.WriteLine($"ROR.W #8 (byte swap low word): 0x{result1:X8}");
        
        // Simulate ROL.L #8 (rotate 32-bit left by 8)
        uint result2 = (value << 8) | (value >> 24);
        Console.WriteLine($"ROL.L #8: 0x{result2:X8}");
        
        // Simulate what the current code does (rotate entire 32-bit, mask)
        uint current = value;
        for (int i = 0; i < 8; i++)
        {
            bool lsb = (current & 0x01) != 0;
            current = current >> 1;
            if (lsb) current |= 0x8000; // MOSTBIT[1] for word
        }
        current &= 0xFFFF; // MASKBIT[1]
        uint result3 = (value & 0xFFFF0000) | current;
        Console.WriteLine($"Current code (rotate 32-bit, mask): 0x{result3:X8}");
        
        // What if we rotate entire 32-bit by 8 (not just low word)?
        uint rotated32 = value;
        for (int i = 0; i < 8; i++)
        {
            bool lsb = (rotated32 & 0x01) != 0;
            rotated32 = rotated32 >> 1;
            if (lsb) rotated32 |= 0x80000000; // MOSTBIT[2] for long
        }
        Console.WriteLine($"ROR.L #8: 0x{rotated32:X8}");
    }
}