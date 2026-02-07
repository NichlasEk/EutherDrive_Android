using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Explicit)]
struct UNION_UINT
{
    [FieldOffset(0)] public uint l;
    [FieldOffset(0)] public ushort w;
    [FieldOffset(2)] public ushort wup;
    [FieldOffset(0)] public byte b0;
    [FieldOffset(1)] public byte b1;
    [FieldOffset(2)] public byte b2;
    [FieldOffset(3)] public byte b3;
}

class Program
{
    static void Main()
    {
        Console.WriteLine("Testing UNION_UINT endianness and word operations:\n");
        
        UNION_UINT reg = new UNION_UINT();
        reg.l = 0x07000000;
        
        Console.WriteLine($"Value: 0x{reg.l:X8}");
        Console.WriteLine($"Bytes: [0x{reg.b0:X2}, 0x{reg.b1:X2}, 0x{reg.b2:X2}, 0x{reg.b3:X2}]");
        Console.WriteLine($"w (bytes 0-1): 0x{reg.w:X4}");
        Console.WriteLine($"wup (bytes 2-3): 0x{reg.wup:X4}");
        Console.WriteLine($"l & 0xFFFF: 0x{reg.l & 0xFFFF:X4}");
        Console.WriteLine($"(l >> 16) & 0xFFFF: 0x{(reg.l >> 16) & 0xFFFF:X4}");
        
        Console.WriteLine("\n--- Testing word operations ---");
        
        // Simulate ANDI.W #$00FF, D0
        uint d0 = 0x07000000;
        Console.WriteLine($"\nD0 before ANDI.W: 0x{d0:X8}");
        
        // Read low 16 bits
        uint lowWord = d0 & 0xFFFF;
        Console.WriteLine($"Low 16 bits: 0x{lowWord:X4}");
        
        // AND with immediate
        uint resultWord = lowWord & 0x00FF;
        Console.WriteLine($"After AND with 0x00FF: 0x{resultWord:X4}");
        
        // Write back preserving high 16 bits
        d0 = (d0 & 0xFFFF0000) | resultWord;
        Console.WriteLine($"D0 after ANDI.W: 0x{d0:X8}");
        
        // What if we read wrong word?
        Console.WriteLine("\n--- What if reading wrong word? ---");
        d0 = 0x07000000;
        uint highWord = (d0 >> 16) & 0xFFFF;
        Console.WriteLine($"High 16 bits: 0x{highWord:X4}");
        resultWord = highWord & 0x00FF;
        Console.WriteLine($"High word AND 0x00FF: 0x{resultWord:X4}");
        
        // What if word operation clears high bits?
        Console.WriteLine("\n--- What if word op clears high bits? ---");
        d0 = 0x07000000;
        d0 = resultWord; // Word operation, high bits cleared?
        Console.WriteLine($"If word op clears high bits: 0x{d0:X8}");
        
        // What the game seems to need
        Console.WriteLine("\n--- What Madou seems to need ---");
        Console.WriteLine($"0x00000700 = 0x{0x00000700:X8}");
        Console.WriteLine($"Bytes: [0x{0x00:X2}, 0x{0x07:X2}, 0x{0x00:X2}, 0x{0x00:X2}]");
        
        // How to get 0x00000700 from 0x00070000?
        Console.WriteLine("\n--- Getting 0x00000700 from 0x00070000 ---");
        uint start = 0x00070000;
        Console.WriteLine($"Start: 0x{start:X8}");
        Console.WriteLine($"Right shift by 8: 0x{start >> 8:X8}");
        Console.WriteLine($"Right shift by 16 then left by 8: 0x{((start >> 16) << 8):X8}");
        
        // What ROL.L #8 actually does
        Console.WriteLine("\n--- What ROL.L #8 does ---");
        start = 0x00070000;
        // ROL.L #8: rotate left 8 bits
        uint rolResult = ((start << 8) | (start >> 24));
        Console.WriteLine($"0x{start:X8} ROL.L #8 = 0x{rolResult:X8}");
    }
}