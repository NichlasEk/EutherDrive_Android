using System;

class Program
{
    static void TestSequence(uint startValue, string rotationType)
    {
        Console.WriteLine($"\n=== Test: {rotationType} on 0x{startValue:X8} ===");
        
        uint d0 = startValue;
        Console.WriteLine($"Start: D0 = 0x{d0:X8}");
        
        // Step 1: Rotation
        uint afterRotation = 0;
        if (rotationType == "ROR.W #8")
        {
            // ROR.W #8 = byte swap of low word
            ushort lowWord = (ushort)(d0 & 0xFFFF);
            ushort rotatedLow = (ushort)((lowWord >> 8) | (lowWord << 8));
            afterRotation = (d0 & 0xFFFF0000) | rotatedLow;
        }
        else if (rotationType == "ROL.L #8")
        {
            // ROL.L #8 = rotate left 32-bit by 8
            afterRotation = (d0 << 8) | (d0 >> 24);
        }
        else if (rotationType == "ROR.L #8")
        {
            // ROR.L #8 = rotate right 32-bit by 8
            afterRotation = (d0 >> 8) | (d0 << 24);
        }
        Console.WriteLine($"After {rotationType}: D0 = 0x{afterRotation:X8}");
        
        // Step 2: ANDI.W #$00FF, D0
        // AND low 16 bits with 0x00FF, preserve high 16 bits
        uint lowWordAfter = afterRotation & 0xFFFF;
        uint andResult = lowWordAfter & 0x00FF;
        uint afterANDI = (afterRotation & 0xFFFF0000) | andResult;
        Console.WriteLine($"After ANDI.W #$00FF, D0: D0 = 0x{afterANDI:X8}");
        Console.WriteLine($"  (low word: 0x{lowWordAfter:X4} & 0x00FF = 0x{andResult:X4})");
        
        // Step 3: ASL.W #5, D0
        // Shift low 16 bits left by 5, preserve high 16 bits
        uint shiftResult = andResult << 5;
        uint afterASL = (afterANDI & 0xFFFF0000) | (shiftResult & 0xFFFF);
        Console.WriteLine($"After ASL.W #5, D0: D0 = 0x{afterASL:X8}");
        Console.WriteLine($"  (0x{andResult:X4} << 5 = 0x{shiftResult:X4})");
        
        // What Madou needs: 0x0000E000 after ASL
        // That means after ANDI: 0x00000700 (0x0700 << 5 = 0xE000)
        Console.WriteLine($"Madou needs after ASL: ~0x0000E000");
        Console.WriteLine($"Which means after ANDI: ~0x00000700");
        Console.WriteLine($"Our result after ANDI: 0x{afterANDI:X8}");
        Console.WriteLine($"Match: {(afterANDI == 0x00000700 ? "YES" : "NO")}");
    }
    
    static void Main()
    {
        Console.WriteLine("Testing Madou Monogatari palette calculation sequence");
        Console.WriteLine("Start value: D0 = 0x00070000");
        Console.WriteLine("Sequence: ROTATION → ANDI.W #$00FF, D0 → ASL.W #5, D0");
        
        TestSequence(0x00070000, "ROR.W #8");
        TestSequence(0x00070000, "ROL.L #8");
        TestSequence(0x00070000, "ROR.L #8");
        
        // What if the rotation produces 0x07000000 (from debug)?
        Console.WriteLine("\n=== What if rotation produces 0x07000000 (from debug) ===");
        uint d0 = 0x07000000;
        Console.WriteLine($"Start: D0 = 0x{d0:X8}");
        
        // ANDI.W #$00FF, D0
        uint lowWord = d0 & 0xFFFF; // 0x0000
        uint andResult = lowWord & 0x00FF; // 0x0000
        uint afterANDI = (d0 & 0xFFFF0000) | andResult; // 0x07000000
        Console.WriteLine($"After ANDI.W #$00FF, D0: D0 = 0x{afterANDI:X8}");
        
        // ASL.W #5, D0
        uint shiftResult = andResult << 5; // 0x0000
        uint afterASL = (afterANDI & 0xFFFF0000) | (shiftResult & 0xFFFF); // 0x07000000
        Console.WriteLine($"After ASL.W #5, D0: D0 = 0x{afterASL:X8}");
        
        Console.WriteLine("\n=== Analysis ===");
        Console.WriteLine("For Madou to get 0x0000E000 after ASL:");
        Console.WriteLine("1. After ANDI must be 0x00000700");
        Console.WriteLine("2. Which means rotation must produce 0x00000700");
        Console.WriteLine("3. ROR.L #8 on 0x00070000 gives 0x00000700");
        Console.WriteLine("4. But debug shows 0x07000000 (ROL.L #8)");
        Console.WriteLine("\nEither:");
        Console.WriteLine("A) Debug is wrong/old");
        Console.WriteLine("B) There's another bug elsewhere");
        Console.WriteLine("C) Our understanding is wrong");
    }
}