using System;

namespace TestMadouFix
{
    class Program
    {
        static void TestShiftMask(uint value, int size, string op)
        {
            uint mask = size == 0 ? 0xFF : (size == 1 ? 0xFFFF : 0xFFFFFFFF);
            uint shifted = (value << 1) & mask;
            Console.WriteLine($"{op}: 0x{value:X8} << 1 & 0x{mask:X8} = 0x{shifted:X8}");
            
            // Test what happens without mask
            uint shiftedNoMask = value << 1;
            Console.WriteLine($"  Without mask: 0x{value:X8} << 1 = 0x{shiftedNoMask:X8} (WRONG for size={size})");
        }
        
        static void Main()
        {
            Console.WriteLine("Testing Madou fix assumptions...\n");
            
            // Test 1: Word shift without mask
            Console.WriteLine("Test 1: Word shift (size=1)");
            TestShiftMask(0x00008000, 1, "ASL.W");
            TestShiftMask(0x00004000, 1, "ASL.W");
            TestShiftMask(0x00000001, 1, "ASL.W");
            
            Console.WriteLine("\nTest 2: Byte shift (size=0)");  
            TestShiftMask(0x00000080, 0, "ASL.B");
            TestShiftMask(0x00000040, 0, "ASL.B");
            
            Console.WriteLine("\nTest 3: Long shift (size=2)");
            TestShiftMask(0x80000000, 2, "ASL.L");
            TestShiftMask(0x40000000, 2, "ASL.L");
            
            // Test 4: ANDI.W bug
            Console.WriteLine("\nTest 4: ANDI.W #$00FF,D0 with D0=0x07000000");
            uint d0 = 0x07000000;
            uint low16 = d0 & 0x0000FFFF;  // Get low 16 bits: 0x0000
            low16 = low16 & 0x00FF;        // AND with 0x00FF: 0x0000
            uint high16 = d0 & 0xFFFF0000; // Preserve high 16 bits: 0x07000000
            uint result = high16 | low16;  // Combine: 0x07000000
            Console.WriteLine($"Correct: 0x{d0:X8} & 0x000000FF = 0x{result:X8} (high bits preserved)");
            Console.WriteLine($"Wrong:   0x{d0:X8} & 0x000000FF = 0x{(d0 & 0x000000FF):X8} (high bits cleared)");
            
            // Test 5: Complete sequence
            Console.WriteLine("\nTest 5: Complete Madou sequence");
            d0 = 0x00070000;
            Console.WriteLine($"Start: D0 = 0x{d0:X8}");
            
            // rol.l #8,d0
            d0 = (d0 << 8) | (d0 >> 24);
            Console.WriteLine($"After rol.l #8,d0: 0x{d0:X8} (should be 0x07000000)");
            
            // andi.w #$00FF,d0  
            low16 = d0 & 0x0000FFFF;
            low16 = low16 & 0x00FF;
            high16 = d0 & 0xFFFF0000;
            d0 = high16 | low16;
            Console.WriteLine($"After andi.w #$00FF,d0: 0x{d0:X8} (should be 0x07000000)");
            
            // asl.w #5,d0
            low16 = d0 & 0x0000FFFF;
            low16 = (low16 << 5) & 0x0000FFFF;
            d0 = (d0 & 0xFFFF0000) | low16;
            Console.WriteLine($"After asl.w #5,d0: 0x{d0:X8} (should be 0x07000000)");
            
            uint index = (d0 >> 16) & 0xFF;
            Console.WriteLine($"Index = (D0 >> 16) & 0xFF = 0x{index:X2} (should be 0x07)");
            
            if (index == 0x07)
                Console.WriteLine("\nSUCCESS: All fixes are conceptually correct!");
            else
                Console.WriteLine($"\nFAILURE: Index is 0x{index:X2}, should be 0x07");
        }
    }
}