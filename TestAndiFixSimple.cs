using System;

namespace TestAndiFix
{
    class Program
    {
        static void Main()
        {
            Console.WriteLine("Testing ANDI.W fix logic...");
            
            // Test 1: Simulate the buggy behavior
            Console.WriteLine("\nTest 1: Buggy behavior (old code)");
            uint d0 = 0x07000000;
            uint andiResult = d0 & 0x000000FF;  // This is what the buggy code does
            Console.WriteLine($"D0=0x{d0:X8} ANDI.W #$00FF,D0 = 0x{andiResult:X8}");
            Console.WriteLine($"Expected: 0x07000000 (high 16 bits preserved)");
            Console.WriteLine($"Actual:   0x{andiResult:X8} (WRONG!)");
            
            // Test 2: Simulate the fixed behavior
            Console.WriteLine("\nTest 2: Fixed behavior (new code)");
            d0 = 0x07000000;
            uint low16 = d0 & 0x0000FFFF;  // Get low 16 bits
            low16 = low16 & 0x00FF;        // Apply ANDI to low 16 bits
            uint high16 = d0 & 0xFFFF0000; // Preserve high 16 bits
            andiResult = high16 | low16;   // Combine
            Console.WriteLine($"D0=0x{d0:X8} ANDI.W #$00FF,D0 = 0x{andiResult:X8}");
            Console.WriteLine($"Expected: 0x07000000 (high 16 bits preserved)");
            Console.WriteLine($"Actual:   0x{andiResult:X8} (CORRECT!)");
            
            // Test 3: Complete Madou sequence
            Console.WriteLine("\nTest 3: Complete Madou sequence");
            d0 = 0x00070000;
            Console.WriteLine($"Start: D0 = 0x{d0:X8}");
            
            // rol.l #8,d0: 0x00070000 -> 0x07000000
            d0 = (d0 << 8) | (d0 >> 24);
            Console.WriteLine($"After rol.l #8,d0: D0 = 0x{d0:X8}");
            
            // andi.w #$00FF,d0: should give 0x07000000 (high 16 bits preserved)
            low16 = d0 & 0x0000FFFF;
            low16 = low16 & 0x00FF;
            high16 = d0 & 0xFFFF0000;
            d0 = high16 | low16;
            Console.WriteLine($"After andi.w #$00FF,d0: D0 = 0x{d0:X8}");
            
            // asl.w #5,d0: shift low 16 bits by 5
            // 0x07000000 -> low 16 bits: 0x0000, shift left 5: 0x0000
            low16 = d0 & 0x0000FFFF;
            low16 = (low16 << 5) & 0x0000FFFF;
            d0 = (d0 & 0xFFFF0000) | low16;
            Console.WriteLine($"After asl.w #5,d0: D0 = 0x{d0:X8}");
            
            // Final index should be (D0 >> 16) & 0xFF = 0x07
            uint index = (d0 >> 16) & 0xFF;
            Console.WriteLine($"Index = (D0 >> 16) & 0xFF = 0x{index:X2}");
            
            if (index == 0x07)
                Console.WriteLine("\nSUCCESS: Fix would work correctly!");
            else
                Console.WriteLine($"\nFAILURE: Index is 0x{index:X2}, should be 0x07");
        }
    }
}