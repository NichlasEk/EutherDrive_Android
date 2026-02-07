using System;

namespace TestMadouFixVerification
{
    class Program
    {
        static void Main()
        {
            Console.WriteLine("Testing Madou ANDI.W fix...\n");
            
            // Simulate the exact Madou sequence:
            // D0 = 0x00070000
            // rol.l #8,d0  -> D0 = 0x07000000
            // andi.w #$00FF,d0 -> Should keep D0 = 0x07000000
            // asl.w #1,d0 -> Should keep D0 = 0x07000000 (0x0000 << 1 = 0x0000)
            
            uint d0 = 0x00070000;
            Console.WriteLine($"Start: D0 = 0x{d0:X8}");
            
            // rol.l #8,d0
            d0 = (d0 << 8) | (d0 >> 24);
            Console.WriteLine($"After rol.l #8,d0: 0x{d0:X8} (should be 0x07000000)");
            
            // andi.w #$00FF,d0 - OLD BUGGY VERSION
            uint oldBuggyResult = d0 & 0x000000FF;
            Console.WriteLine($"OLD BUGGY andi.w #$00FF,d0: 0x{oldBuggyResult:X8} (WRONG! loses high bits)");
            
            // andi.w #$00FF,d0 - NEW FIXED VERSION
            uint lowBits = d0 & 0x0000FFFF;  // Get low 16 bits: 0x0000
            lowBits = lowBits & 0x00FF;      // AND with 0x00FF: 0x0000
            uint highBits = d0 & 0xFFFF0000; // Preserve high 16 bits: 0x07000000
            uint newFixedResult = highBits | lowBits;
            Console.WriteLine($"NEW FIXED andi.w #$00FF,d0: 0x{newFixedResult:X8} (CORRECT! preserves high bits)");
            
            // asl.w #1,d0
            lowBits = newFixedResult & 0x0000FFFF;  // Get low 16 bits: 0x0000
            lowBits = (lowBits << 1) & 0x0000FFFF;  // Shift left: 0x0000
            uint aslResult = (newFixedResult & 0xFFFF0000) | lowBits;
            Console.WriteLine($"After asl.w #1,d0: 0x{aslResult:X8} (should be 0x07000000)");
            
            // Calculate index for table lookup
            // In Madou code: after asl.w #1,d0, it uses D0 as index
            // The actual index calculation in Madou is more complex
            // But for our test, we want to see if high bits are preserved
            Console.WriteLine($"\nD0 after complete sequence: 0x{aslResult:X8}");
            Console.WriteLine($"High 16 bits: 0x{(aslResult >> 16):X4}");
            Console.WriteLine($"Low 16 bits: 0x{(aslResult & 0xFFFF):X4}");
            
            // The key is: D0 should be 0x07000000, not 0x00000000
            if (aslResult == 0x07000000)
                Console.WriteLine("SUCCESS: D0 high bits preserved! Index will be non-zero.");
            else if (aslResult == 0x00000000)
                Console.WriteLine("FAILURE: D0 high bits lost! Index will be zero.");
            else
                Console.WriteLine($"UNEXPECTED: D0 = 0x{aslResult:X8}");
                
            Console.WriteLine("\nThe bug was in ANDI.W instruction not preserving high bits!");
            Console.WriteLine("After fixing ANDI.W, D0 remains 0x07000000 after andi.w #$00FF,d0");
            Console.WriteLine("This gives index 0x07 instead of 0x00, pointing to correct table data.");
        }
    }
}