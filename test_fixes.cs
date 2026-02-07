using System;

class Program
{
    static void TestASL()
    {
        Console.WriteLine("=== Testing ASL.W #5 ===");
        
        // What should happen: 0x0700 << 5 = 0xE000
        ushort word = 0x0700;
        ushort shifted = (ushort)(word << 5);
        Console.WriteLine($"0x{word:X4} << 5 = 0x{shifted:X4} (expected: 0xE000)");
        
        // What old code does: shifts entire 32-bit value
        uint oldValue = 0x00000700;
        uint oldShifted = oldValue << 5; // 0x00000E00
        oldShifted &= 0xFFFF; // 0x0E00
        Console.WriteLine($"Old code (shift 32-bit): 0x{oldValue:X8} << 5 & 0xFFFF = 0x{oldShifted:X4}");
        
        // What new code should do: extract low word, shift, combine
        uint newValue = 0x00000700;
        ushort lowWord = (ushort)(newValue & 0xFFFF);
        ushort newLowWord = (ushort)(lowWord << 5);
        uint newResult = (newValue & 0xFFFF0000) | newLowWord;
        Console.WriteLine($"New code (shift low word): 0x{newValue:X8} -> 0x{newResult:X8} (low: 0x{newLowWord:X4})");
    }
    
    static void TestROR()
    {
        Console.WriteLine("\n=== Testing ROR.L #8 ===");
        
        uint value = 0x00070000;
        uint rorResult = (value >> 8) | (value << 24);
        Console.WriteLine($"ROR.L #8: 0x{value:X8} -> 0x{rorResult:X8}");
        
        // What Madou needs after ANDI: 0x00000700
        // After ROR.L #8: 0x00000700 ✓
    }
    
    static void TestANDI()
    {
        Console.WriteLine("\n=== Testing ANDI.W #$00FF ===");
        
        uint[] testValues = { 0x00000700, 0x07000000, 0x00070000 };
        
        foreach (uint value in testValues)
        {
            ushort lowWord = (ushort)(value & 0xFFFF);
            ushort andResult = (ushort)(lowWord & 0x00FF);
            uint result = (value & 0xFFFF0000) | andResult;
            
            Console.WriteLine($"0x{value:X8}: low=0x{lowWord:X4} & 0x00FF = 0x{andResult:X4} -> 0x{result:X8}");
        }
        
        Console.WriteLine("\nProblem: ANDI.W #$00FF on 0x00000700 gives 0x00000000");
        Console.WriteLine("Madou needs 0x00000700 after ANDI");
        Console.WriteLine("This is IMPOSSIBLE with ANDI.W #$00FF!");
        Console.WriteLine("0x0700 & 0x00FF = 0x0000 always");
    }
    
    static void Main()
    {
        Console.WriteLine("Testing our fixes for Madou corruption");
        
        TestASL();
        TestROR();
        TestANDI();
        
        Console.WriteLine("\n=== Conclusion ===");
        Console.WriteLine("1. ASL fix should work: 0x0700 << 5 = 0xE000");
        Console.WriteLine("2. ROR.L #8 gives 0x00000700 from 0x00070000");
        Console.WriteLine("3. ANDI.W #$00FF CANNOT give 0x00000700 from 0x00000700");
        Console.WriteLine("4. Either: A) Our analysis is wrong B) More bugs C) Not ANDI.W");
    }
}