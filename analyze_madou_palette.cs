using System;

namespace AnalyzeMadouPalette
{
    class Program
    {
        static void Main()
        {
            Console.WriteLine("Analyzing Madou palette routine logic...\n");
            
            // Full routine at 0x013A46 based on ROM bytes:
            // 49 F9 00 FF 94 78      lea     $FF9478,A4
            // E1 98                  rol.l   #8,D0
            // 48 E7 80 00            movem.l D0,-(A7)   ; Save D0 on stack
            // 02 40 00 FF            andi.w  #$00FF,D0  ; Extract low byte of D0
            // EB 40                  asl.w   #5,D0      ; Multiply by 32
            // 47 F9 00 07 8A 8C      lea     $078A8C,A3 ; Base table
            // D6 C0                  adda.w  D0,A3      ; Add offset
            // 28 DB                  move.l  (A3)+,(A4)+ ; Copy 8 longs
            // 28 DB                  move.l  (A3)+,(A4)+
            // 28 DB                  move.l  (A3)+,(A4)+
            // 28 DB                  move.l  (A3)+,(A4)+
            // 28 DB                  move.l  (A3)+,(A4)+
            // 28 DB                  move.l  (A3)+,(A4)+
            // 28 DB                  move.l  (A3)+,(A4)+
            // 28 DB                  move.l  (A3)+,(A4)+
            // ... then restore D0 from stack
            
            // Input: D0 = 0x00070000 (from logs)
            uint d0 = 0x00070000;
            Console.WriteLine($"Input D0 = 0x{d0:X8}");
            
            // Step-by-step simulation
            Console.WriteLine("\n1. ROL.L #8,D0");
            d0 = (d0 << 8) | (d0 >> 24);
            Console.WriteLine($"   D0 = 0x{d0:X8} (0x07000000)");
            
            Console.WriteLine("\n2. MOVEM.L D0,-(A7)");
            Console.WriteLine("   Saves D0 on stack (0x07000000)");
            uint savedD0 = d0;
            
            Console.WriteLine("\n3. ANDI.W #$00FF,D0");
            Console.WriteLine("   AND low 16 bits (0x0000) with 0x00FF = 0x0000");
            uint lowWord = d0 & 0x0000FFFF;
            lowWord = lowWord & 0x00FF;
            d0 = (d0 & 0xFFFF0000) | lowWord;
            Console.WriteLine($"   D0 = 0x{d0:X8} (still 0x07000000, low word 0x0000)");
            
            Console.WriteLine("\n4. ASL.W #5,D0");
            Console.WriteLine("   Shift low word left 5 bits: 0x0000 << 5 = 0x0000");
            lowWord = d0 & 0x0000FFFF;
            lowWord = (lowWord << 5) & 0x0000FFFF;
            d0 = (d0 & 0xFFFF0000) | lowWord;
            Console.WriteLine($"   D0 = 0x{d0:X8} (still 0x07000000)");
            
            Console.WriteLine("\n5. Calculate table offset");
            ushort offset = (ushort)(d0 & 0xFFFF);
            Console.WriteLine($"   Offset = 0x{offset:X4} (0x0000)");
            
            uint tableBase = 0x00078A8C;
            uint tableAddr = tableBase + offset;
            Console.WriteLine($"   Table address = 0x{tableBase:X8} + 0x{offset:X4} = 0x{tableAddr:X8}");
            
            Console.WriteLine("\n--- PROBLEM ---");
            Console.WriteLine("Offset is 0x0000, but we want 0x00E0 (224 decimal)");
            Console.WriteLine("224 = 7 * 32, and D0 high byte = 0x07");
            
            Console.WriteLine("\n--- POSSIBLE BUGS ---");
            Console.WriteLine("1. ANDI.W #$00FF should extract BYTE 0x07, not 0x00");
            Console.WriteLine("   But it extracts from low 16 bits, not high byte");
            
            Console.WriteLine("\n2. Maybe D0 input is wrong? Should be 0x000700E0?");
            Console.WriteLine("   But logs show D0 = 0x00070000 at JSR 0x013A46");
            
            Console.WriteLine("\n3. Maybe ANDI.W acts on different part of D0?");
            Console.WriteLine("   After ROL #8: D0 = 0x07000000");
            Console.WriteLine("   Byte positions: [0x07][0x00][0x00][0x00]");
            Console.WriteLine("   ANDI.W #$00FF masks bytes [0x00][0x00]");
            
            Console.WriteLine("\n--- KEY INSIGHT ---");
            Console.WriteLine("The code seems designed to extract a BYTE and multiply by 32");
            Console.WriteLine("But it's extracting from wrong position!");
            
            Console.WriteLine("\nWhat if the intention is:");
            Console.WriteLine("1. ROL.L #8 moves byte 0x07 to high byte position");
            Console.WriteLine("2. Want to extract that byte (0x07)");
            Console.WriteLine("3. Should use ANDI.B #$FF,D0 or SWAP/LSR etc.");
            Console.WriteLine("4. But code uses ANDI.W #$00FF which gets wrong byte");
            
            Console.WriteLine("\n--- CHECK ROM BYTES AGAIN ---");
            Console.WriteLine("Bytes: 02 40 00 FF = ANDI.W #$00FF,D0");
            Console.WriteLine("This is definitely ANDI.W (word), not ANDI.B (byte)");
            
            Console.WriteLine("\n--- HYPOTHESIS ---");
            Console.WriteLine("Maybe the ROM has a BUG, or our understanding is wrong");
            Console.WriteLine("OR: Maybe D0 should be 0x07000000 BEFORE the routine?");
            Console.WriteLine("OR: Maybe there's pre-processing before 0x013A46?");
            
            // Check: what if D0 = 0x07000000 before ROL.L #8?
            Console.WriteLine("\n--- TEST: D0 = 0x07000000 at start ---");
            d0 = 0x07000000;
            Console.WriteLine($"Start D0 = 0x{d0:X8}");
            
            d0 = (d0 << 8) | (d0 >> 24); // ROL.L #8
            Console.WriteLine($"After ROL.L #8: 0x{d0:X8} (0x00000700)");
            
            // Now ANDI.W #$00FF gets 0x0700 & 0x00FF = 0x0000
            // Still wrong!
            
            Console.WriteLine("\n--- CONCLUSION ---");
            Console.WriteLine("The code logic seems broken UNLESS:");
            Console.WriteLine("1. D0 contains byte value in bits 8-15 (not 24-31)");
            Console.WriteLine("2. OR: Table at 0x078A8C+0x0000 actually has data (not zeros)");
            Console.WriteLine("3. OR: There's different execution path we're not seeing");
        }
    }
}