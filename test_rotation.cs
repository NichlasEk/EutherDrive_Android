using System;

class Program
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    struct UNION_UINT
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public uint l;
        [System.Runtime.InteropServices.FieldOffset(0)] public ushort w;
        [System.Runtime.InteropServices.FieldOffset(2)] public ushort wup;
        [System.Runtime.InteropServices.FieldOffset(0)] public byte b0;
        [System.Runtime.InteropServices.FieldOffset(1)] public byte b1;
        [System.Runtime.InteropServices.FieldOffset(2)] public byte b2;
        [System.Runtime.InteropServices.FieldOffset(3)] public byte b3;
    }

    static void Main()
    {
        // Test ROR.W #8 on 0x00070000
        UNION_UINT reg = new UNION_UINT();
        reg.l = 0x00070000;
        
        Console.WriteLine($"Initial: 0x{reg.l:X8}");
        Console.WriteLine($"Bytes: [{reg.b3:X2},{reg.b2:X2},{reg.b1:X2},{reg.b0:X2}]");
        Console.WriteLine($"w (low): 0x{reg.w:X4}");
        Console.WriteLine($"wup (high): 0x{reg.wup:X4}");
        
        // ROR.W #8 means rotate right word by 8 bits = byte swap within word
        // We need to rotate the LOW word (w) by 8 bits
        ushort lowWord = reg.w;
        ushort rotatedLow = (ushort)((lowWord >> 8) | (lowWord << 8));
        
        // The high word (wup) should remain unchanged for ROR.W
        ushort highWord = reg.wup;
        ushort rotatedHigh = (ushort)((highWord >> 8) | (highWord << 8));
        
        Console.WriteLine($"\nAfter ROR.W #8:");
        Console.WriteLine($"Low word: 0x{lowWord:X4} -> 0x{rotatedLow:X4}");
        Console.WriteLine($"High word: 0x{highWord:X4} -> 0x{rotatedHigh:X4}");
        
        // Result should be: high word byte-swapped in high position
        reg.w = rotatedLow;
        reg.wup = rotatedHigh;
        Console.WriteLine($"Full result: 0x{reg.l:X8}");
        Console.WriteLine($"Bytes: [{reg.b3:X2},{reg.b2:X2},{reg.b1:X2},{reg.b0:X2}]");
        
        // What Madou seems to expect:
        Console.WriteLine($"\nWhat Madou might expect (0x00000700):");
        Console.WriteLine($"Bytes for 0x00000700: [00,07,00,00]");
        Console.WriteLine($"This would be high word (0x0700) moved to low word position");
        
        // Test ANDI.W #$00FF on 0x07000000
        Console.WriteLine($"\n--- Testing ANDI.W #$00FF ---");
        reg.l = 0x07000000;
        Console.WriteLine($"Input: 0x{reg.l:X8}");
        
        // ANDI.W operates on low 16 bits
        ushort lowBits = reg.w; // 0x0000
        ushort immediate = 0x00FF;
        ushort resultLow = (ushort)(lowBits & immediate); // 0x0000
        
        // Preserve high 16 bits
        uint result = (reg.l & 0xFFFF0000) | resultLow;
        Console.WriteLine($"ANDI.W result: 0x{result:X8} (expected: 0x07000000)");
        
        // What if ANDI.W operated on high word?
        ushort highBits = reg.wup; // 0x0700
        ushort resultHigh = (ushort)(highBits & immediate); // 0x0700 & 0x00FF = 0x0700
        uint resultAlt = (uint)((resultHigh << 16) | (reg.l & 0xFFFF)); // 0x07000000
        Console.WriteLine($"If ANDI.W on high word: 0x{resultAlt:X8}");
        
        // What Madou needs: 0x00000700
        Console.WriteLine($"\nMadou needs: 0x00000700");
        Console.WriteLine($"This is high word (0x0700) in low word position");
    }
}