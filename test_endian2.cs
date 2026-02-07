using System;

class Program
{
    static void TestEndian(string label, uint value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Console.WriteLine($"{label}: 0x{value:X8}");
        Console.WriteLine($"  Little-endian bytes: [{bytes[0]:X2},{bytes[1]:X2},{bytes[2]:X2},{bytes[3]:X2}]");
        
        // Big-endian representation
        uint bigEndian = ((value & 0xFF000000) >> 24) |
                        ((value & 0x00FF0000) >> 8) |
                        ((value & 0x0000FF00) << 8) |
                        ((value & 0x000000FF) << 24);
        byte[] bigBytes = BitConverter.GetBytes(bigEndian);
        Console.WriteLine($"  Big-endian bytes:    [{bigBytes[3]:X2},{bigBytes[2]:X2},{bigBytes[1]:X2},{bigBytes[0]:X2}]");
        
        // What a 68000 would see for word access
        ushort lowWordBE = (ushort)(value >> 16);  // Most significant word in big-endian
        ushort highWordBE = (ushort)(value & 0xFFFF);  // Least significant word in big-endian
        Console.WriteLine($"  68000 word access:");
        Console.WriteLine($"    Low word (bytes 0-1 in BE): 0x{lowWordBE:X4}");
        Console.WriteLine($"    High word (bytes 2-3 in BE): 0x{highWordBE:X4}");
    }
    
    static void Main()
    {
        Console.WriteLine("=== Testing Endianness ===");
        
        TestEndian("0x00070000", 0x00070000);
        Console.WriteLine();
        TestEndian("0x07000000", 0x07000000);
        Console.WriteLine();
        TestEndian("0x00000700", 0x00000700);
        
        Console.WriteLine("\n=== What Madou Needs ===");
        Console.WriteLine("After ROR.W #8, D0 on 0x00070000:");
        Console.WriteLine("  Should produce: 0x07000000 (high word byte-swapped)");
        Console.WriteLine("  But Madou seems to need: 0x00000700");
        Console.WriteLine("\nIf we store 0x07000000 in little-endian:");
        Console.WriteLine("  Bytes: [00,00,00,07]");
        Console.WriteLine("  w (bytes 0-1): 0x0000");
        Console.WriteLine("  wup (bytes 2-3): 0x0700");
        Console.WriteLine("\nIf 68000 reads word from D0=0x07000000:");
        Console.WriteLine("  Should read low 16 bits of 32-bit register: 0x0000");
        Console.WriteLine("  NOT high 16 bits: 0x0700");
        
        Console.WriteLine("\n=== Hypothesis ===");
        Console.WriteLine("Maybe the bug is in how we STORE the result of ROR.W #8");
        Console.WriteLine("If we store 0x00000700 instead of 0x07000000:");
        Console.WriteLine("  Little-endian bytes: [00,07,00,00]");
        Console.WriteLine("  w (bytes 0-1): 0x0000");
        Console.WriteLine("  wup (bytes 2-3): 0x0007");
        Console.WriteLine("Then ANDI.W #$00FF gives: 0x00000000");
        Console.WriteLine("Preserves high word: 0x00000700");
        Console.WriteLine("That's what Madou needs!");
    }
}