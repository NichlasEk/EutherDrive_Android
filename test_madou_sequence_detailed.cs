using System;

namespace TestMadouSequenceDetailed
{
    class Program
    {
        static void TestSequence()
        {
            Console.WriteLine("Testing Madou palette index calculation sequence");
            Console.WriteLine("================================================");
            
            // The sequence from ROM at 0x013A46:
            // 1. ROL.L #8,D0   (D0 = 0x00070000 from caller)
            // 2. ANDI.W #$00FF,D0
            // 3. ASL.W #5,D0
            // Result should give index to non-zero table data
            
            uint d0 = 0x00070000;
            Console.WriteLine($"Start: D0 = 0x{d0:X8} (from caller at 0x0629C2)");
            Console.WriteLine();
            
            // 1. ROL.L #8,D0
            Console.WriteLine("1. ROL.L #8,D0");
            Console.WriteLine($"   Before: 0x{d0:X8}");
            d0 = (d0 << 8) | (d0 >> 24);
            Console.WriteLine($"   After:  0x{d0:X8} (0x07000000)");
            Console.WriteLine($"   Low word: 0x{(d0 & 0xFFFF):X4}");
            Console.WriteLine($"   High byte of low word: 0x{((d0 >> 8) & 0xFF):X2}");
            Console.WriteLine($"   Low byte: 0x{(d0 & 0xFF):X2}");
            Console.WriteLine();
            
            // 2. ANDI.W #$00FF,D0
            Console.WriteLine("2. ANDI.W #$00FF,D0");
            Console.WriteLine($"   Before: 0x{d0:X8}");
            uint lowWord = d0 & 0x0000FFFF;
            lowWord = lowWord & 0x00FF;
            uint highWord = d0 & 0xFFFF0000;
            d0 = highWord | lowWord;
            Console.WriteLine($"   After:  0x{d0:X8}");
            Console.WriteLine($"   Low word: 0x{(d0 & 0xFFFF):X4} (0x0000 & 0x00FF = 0x0000)");
            Console.WriteLine($"   High word preserved: 0x{(d0 >> 16):X4}");
            Console.WriteLine();
            
            // 3. ASL.W #5,D0  
            Console.WriteLine("3. ASL.W #5,D0");
            Console.WriteLine($"   Before: 0x{d0:X8}");
            lowWord = d0 & 0x0000FFFF;
            lowWord = (lowWord << 5) & 0x0000FFFF;
            d0 = (d0 & 0xFFFF0000) | lowWord;
            Console.WriteLine($"   After:  0x{d0:X8}");
            Console.WriteLine($"   Low word: 0x{(d0 & 0xFFFF):X4} (0x0000 << 5 = 0x0000)");
            Console.WriteLine();
            
            // Index calculation in code: ADDA.W D0,A3 (add low word of D0 to A3)
            Console.WriteLine("Index calculation: ADDA.W D0,A3");
            uint index = d0 & 0xFFFF;
            Console.WriteLine($"   Index (low word of D0): 0x{index:X4}");
            Console.WriteLine($"   As signed: {((short)index):d}");
            Console.WriteLine();
            
            // Base address: A3 = 0x078A8C (from LEA)
            uint baseAddr = 0x078A8C;
            uint finalAddr = baseAddr + index;
            Console.WriteLine($"Base table address: 0x{baseAddr:X6}");
            Console.WriteLine($"Final address: 0x{finalAddr:X6} = 0x{baseAddr:X6} + 0x{index:X4}");
            Console.WriteLine();
            
            // The table at 0x078A8C has zeros for first 0x20 bytes
            // Non-zero data starts at 0x078B6C
            uint dataStart = 0x078B6C;
            uint neededIndex = dataStart - baseAddr;
            Console.WriteLine($"Non-zero data starts at: 0x{dataStart:X6}");
            Console.WriteLine($"Needed index: 0x{neededIndex:X4} (0x{dataStart:X6} - 0x{baseAddr:X6})");
            Console.WriteLine($"Needed low word in D0: 0x{neededIndex:X4}");
            Console.WriteLine();
            
            // What would give us index 0x00E0?
            // 0x00E0 = 0x07 << 5
            // So we need low word = 0x0007 before ASL.W #5
            Console.WriteLine("To get index 0x00E0 (0x07 << 5):");
            Console.WriteLine("  Need low word = 0x0007 before ASL.W #5");
            Console.WriteLine("  After ASL.W #5: 0x0007 << 5 = 0x00E0");
            Console.WriteLine();
            
            // How to get low word = 0x0007?
            // After ROL.L #8: D0 = 0x07000000
            // Low word = 0x0000, high byte of low word = 0x00, low byte = 0x00
            // ANDI.W #$00FF,D0: low word = 0x0000 & 0x00FF = 0x0000
            // So ANDI.W #$00FF gives us 0x0000, not 0x0007
            Console.WriteLine("Problem: ANDI.W #$00FF,D0 gives low word = 0x0000");
            Console.WriteLine("  Not 0x0007 as needed");
            Console.WriteLine();
            
            // What if it's ANDI.W #$FF00,D0?
            Console.WriteLine("What if it's ANDI.W #$FF00,D0?");
            d0 = 0x07000000; // After ROL
            lowWord = d0 & 0x0000FFFF;
            lowWord = lowWord & 0xFF00; // AND with 0xFF00
            d0 = (d0 & 0xFFFF0000) | lowWord;
            Console.WriteLine($"  After ANDI.W #$FF00,D0: 0x{d0:X8}");
            Console.WriteLine($"  Low word: 0x{(d0 & 0xFFFF):X4} (0x0000 & 0xFF00 = 0x0000)");
            Console.WriteLine("  Still 0x0000!");
            Console.WriteLine();
            
            // What if we need high byte of low word (0x07)?
            // After ROL: D0 = 0x07000000, low word = 0x0000, high byte = 0x00
            // Wait, 0x07000000: bytes: 07 00 00 00
            // Low word: 00 00 (0x0000)
            // High byte of low word: 0x00 (not 0x07!)
            Console.WriteLine("Ah! 0x07000000 in bytes: 07 00 00 00");
            Console.WriteLine("  Byte 3 (MSB): 0x07");
            Console.WriteLine("  Byte 2: 0x00");
            Console.WriteLine("  Byte 1: 0x00");
            Console.WriteLine("  Byte 0 (LSB): 0x00");
            Console.WriteLine("  Low word (bytes 1-0): 0x0000");
            Console.WriteLine("  High byte of low word (byte 1): 0x00");
            Console.WriteLine("  Low byte of low word (byte 0): 0x00");
            Console.WriteLine();
            
            // So after ROL.L #8, the 0x07 is in the HIGHEST byte (byte 3)
            // Not in the low word at all!
            // ANDI.W #$00FF,D0 affects low word (bytes 1-0), not high word (bytes 3-2)
            Console.WriteLine("The 0x07 is in high word (bytes 3-2), not low word!");
            Console.WriteLine("ANDI.W #$00FF,D0 affects low word (bytes 1-0)");
            Console.WriteLine("So it can't extract the 0x07!");
            Console.WriteLine();
            
            // Maybe it's ANDI.B #$FF,D0? Affects low byte (byte 0)
            Console.WriteLine("What if it's ANDI.B #$FF,D0?");
            d0 = 0x07000000;
            uint lowByte = d0 & 0x000000FF;
            lowByte = lowByte & 0xFF;
            d0 = (d0 & 0xFFFFFF00) | lowByte;
            Console.WriteLine($"  After ANDI.B #$FF,D0: 0x{d0:X8}");
            Console.WriteLine($"  Low byte: 0x{(d0 & 0xFF):X2} (0x00 & 0xFF = 0x00)");
            Console.WriteLine("  Still 0x00!");
            Console.WriteLine();
            
            // Conclusion: The sequence ROL.L #8 -> ANDI.W #$00FF -> ASL.W #5
            // CANNOT extract the 0x07 from 0x07000000!
            // Something is wrong with our understanding.
            Console.WriteLine("CONCLUSION:");
            Console.WriteLine("The sequence ROL.L #8 -> ANDI.W #$00FF -> ASL.W #5");
            Console.WriteLine("cannot extract 0x07 from 0x07000000!");
            Console.WriteLine("Either:");
            Console.WriteLine("1. The disassembly is wrong");
            Console.WriteLine("2. There's another bug in the emulator");
            Console.WriteLine("3. The ROM has different code paths");
        }
        
        static void Main()
        {
            TestSequence();
        }
    }
}