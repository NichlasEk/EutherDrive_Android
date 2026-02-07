using System;

namespace TestMadouDetailedSequence
{
    class Program
    {
        static void Main()
        {
            Console.WriteLine("Testing Madou palette routine sequence in detail...\n");
            
            // Madou palette routine at 0x013A46:
            // 013A46: 49F9 00FF 9478      lea     $FF9478,A4
            // 013A4C: E198                 rol.l   #8,D0
            // 013A4E: 48E7 8000           movem.l D0,-(A7)
            // 013A52: 0240 00FF           andi.w  #$00FF,D0
            // 013A56: EB40                 asl.w   #1,D0
            // 013A58: 47F9 0007 8A8C      lea     $00078A8C,A3
            // 013A5E: D6C0                 adda.w  D0,A3
            // 013A60: 28DB                 move.l  (A3)+,(A4)+
            // 013A62: 28DB                 move.l  (A3)+,(A4)+
            // 013A64: 28DB                 move.l  (A3)+,(A4)+
            // 013A66: 28DB                 move.l  (A3)+,(A4)+
            // 013A68: 28DB                 move.l  (A3)+,(A4)+
            // 013A6A: 28DB                 move.l  (A3)+,(A4)+
            // 013A6C: 28DB                 move.l  (A3)+,(A4)+
            // 013A6E: 28DB                 move.l  (A3)+,(A4)+
            
            // Input: D0 = 0x00070000 (from caller at 0x0629C2)
            uint d0 = 0x00070000;
            Console.WriteLine($"Start: D0 = 0x{d0:X8}");
            
            // Step 1: rol.l #8,d0
            // ROL.L #8: Rotate left 8 bits within 32-bit register
            // 0x00070000 -> rotate left 8 bits:
            // Binary: 0000 0000 0000 0111 0000 0000 0000 0000
            // After ROL #8: 0000 0111 0000 0000 0000 0000 0000 0000 = 0x07000000
            d0 = (d0 << 8) | (d0 >> 24);
            Console.WriteLine($"After rol.l #8,d0: 0x{d0:X8} (expected: 0x07000000)");
            
            // Step 2: andi.w #$00FF,d0
            // ANDI.W affects only low 16 bits, preserves high 16 bits
            // D0 = 0x07000000, low word = 0x0000
            // 0x0000 & 0x00FF = 0x0000
            // Result: high 16 bits preserved (0x0700), low 16 bits = 0x0000
            uint lowWord = d0 & 0x0000FFFF;
            lowWord = lowWord & 0x00FF;
            d0 = (d0 & 0xFFFF0000) | lowWord;
            Console.WriteLine($"After andi.w #$00FF,d0: 0x{d0:X8} (expected: 0x07000000)");
            
            // Step 3: asl.w #1,d0
            // ASL.W affects only low 16 bits, preserves high 16 bits
            // Low word = 0x0000 << 1 = 0x0000
            lowWord = d0 & 0x0000FFFF;
            lowWord = (lowWord << 1) & 0x0000FFFF;
            d0 = (d0 & 0xFFFF0000) | lowWord;
            Console.WriteLine($"After asl.w #1,d0: 0x{d0:X8} (expected: 0x07000000)");
            
            // Step 4: Calculate table offset
            // D0 is used as WORD offset (add.w D0,A3)
            // D0 = 0x07000000, but only low 16 bits matter for adda.w
            // Low word = 0x0000
            ushort wordOffset = (ushort)(d0 & 0xFFFF);
            Console.WriteLine($"Word offset for table: 0x{wordOffset:X4} (from low 16 bits of D0)");
            
            // Table base: 0x00078A8C
            uint tableBase = 0x00078A8C;
            uint tableAddress = tableBase + wordOffset;
            Console.WriteLine($"Table address: 0x{tableBase:X8} + 0x{wordOffset:X4} = 0x{tableAddress:X8}");
            
            // What does this mean?
            // If D0 = 0x07000000, word offset = 0x0000 -> table address = 0x078A8C
            // If D0 should give offset 0x00E0 (224 decimal), we need D0 low word = 0x00E0
            
            Console.WriteLine("\n--- ANALYSIS ---");
            Console.WriteLine("Problem: D0 low word is 0x0000, not 0x00E0!");
            Console.WriteLine("Expected: D0 should be 0x000700E0 or similar to get offset 0x00E0");
            Console.WriteLine("Actual: D0 = 0x00070000 from caller");
            
            Console.WriteLine("\nPossible issues:");
            Console.WriteLine("1. Caller at 0x0629C2 passes wrong D0 value");
            Console.WriteLine("2. ROL/ANDI/ASL sequence is wrong - maybe should extract byte, not word?");
            Console.WriteLine("3. Maybe D0 high byte (0x07) should become offset 0x00E0 (7 * 32 = 224)");
            
            // Let's check: if we want offset 0x00E0 = 224 decimal
            // 224 / 32 = 7 (0x07) - matches D0 high byte!
            // So maybe the code should extract byte 0x07 and multiply by 32
            
            Console.WriteLine("\nAlternative interpretation:");
            Console.WriteLine("D0 = 0x00070000 -> high byte = 0x07");
            Console.WriteLine("Want offset = 0x07 * 32 = 0xE0 = 224 decimal");
            Console.WriteLine("But code uses ROL #8 -> 0x07000000 -> ANDI.W #$00FF -> extracts 0x00, not 0x07!");
            
            Console.WriteLine("\nBUG FOUND: ANDI.W #$00FF extracts LOW byte (0x00), not HIGH byte (0x07)!");
            Console.WriteLine("Should extract byte 0x07 from position 16-23, not byte 0x00 from position 0-7");
            Console.WriteLine("Maybe should use ANDI.B #$FF,D0 or different masking?");
            
            // Check ROM bytes at 0x013A52: 0240 00FF = andi.w #$00FF,D0
            // This ANDs the LOW word (bits 0-15) with 0x00FF
            // After ROL #8: D0 = 0x07000000, low word = 0x0000
            
            Console.WriteLine("\nSolution: Maybe the code is wrong or we misunderstand the data format");
            Console.WriteLine("OR: Maybe D0 should be 0x000700E0 from the start, not 0x00070000");
        }
    }
}