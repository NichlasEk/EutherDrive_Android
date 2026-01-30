using System;

class TestRol
{
    static void Main()
    {
        Console.WriteLine("Testing ROL.L #8,D0 with D0=0x00070000");
        
        uint d0 = 0x00070000;
        Console.WriteLine($"Start: D0 = 0x{d0:X8}");
        
        // ROL.L #8,D0: rotate left 8 bits
        // Bits 31-8 become bits 23-0, bits 7-0 become bits 31-24
        uint result = (d0 << 8) | (d0 >> 24);
        Console.WriteLine($"After rol.l #8,d0: D0 = 0x{result:X8}");
        Console.WriteLine($"Expected: 0x07000000");
        
        // ANDI.W #$00FF,D0
        // AND low 16 bits with 0x00FF
        uint andi_result = result & 0x0000FFFF; // Get low 16 bits
        andi_result = andi_result & 0x00FF;     // AND with 0x00FF
        // On 68000: high 16 bits unchanged, low 16 bits ANDed with 0x00FF
        uint final_andi = (result & 0xFFFF0000) | andi_result;
        Console.WriteLine($"After andi.w #$00FF,d0: D0 = 0x{final_andi:X8}");
        Console.WriteLine($"Low 16 bits: 0x{andi_result:X4}");
        
        // ASL.W #5,D0
        // Shift low 16 bits left by 5
        uint low16 = final_andi & 0x0000FFFF;
        low16 = (low16 << 5) & 0x0000FFFF;
        uint asl_result = (final_andi & 0xFFFF0000) | low16;
        Console.WriteLine($"After asl.w #5,d0: D0 = 0x{asl_result:X8}");
        Console.WriteLine($"Low 16 bits: 0x{low16:X4}");
        
        // What we need: low 16 bits = 0x00E0 (0x07 << 5)
        uint needed = 0x07 << 5;
        Console.WriteLine($"\nWhat we need: low 16 bits = 0x{needed:X4} (0x07 << 5)");
        Console.WriteLine($"What we have: low 16 bits = 0x{low16:X4}");
        
        // Alternative: maybe ANDI.W takes the low BYTE, not low WORD?
        uint low_byte = result & 0x000000FF;
        Console.WriteLine($"\nAlternative: low BYTE of 0x{result:X8} = 0x{low_byte:X2}");
        uint asl_from_byte = (low_byte << 5) & 0x0000FFFF;
        Console.WriteLine($"After asl.w #5 from byte: 0x{asl_from_byte:X4}");
    }
}