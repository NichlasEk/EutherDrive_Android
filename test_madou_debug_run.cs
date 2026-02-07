using System;
using System.IO;

namespace TestMadouDebugRun
{
    class Program
    {
        static void Main()
        {
            Console.WriteLine("Madou Monogatari Debug Test Runner");
            Console.WriteLine("===================================");
            
            // The problematic sequence from Madou ROM at 0x013A46-0x013A60
            // Based on analysis from test_madou_sequence_detailed.cs
            
            Console.WriteLine("\nSequence analysis:");
            Console.WriteLine("------------------");
            
            // Address 0x013A46: ROL.L #8,D0
            // Input: D0 = 0x00070000 (from caller at 0x0629C2)
            // Output: D0 = 0x07000000
            
            uint d0 = 0x00070000;
            Console.WriteLine($"0x013A46: ROL.L #8,D0");
            Console.WriteLine($"  Input: 0x{d0:X8}");
            d0 = (d0 << 8) | (d0 >> 24); // ROL.L #8
            Console.WriteLine($"  Output: 0x{d0:X8}");
            
            // Check bytes in big-endian
            byte[] bytesBE = BitConverter.GetBytes(d0);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytesBE);
            Console.WriteLine($"  Bytes (BE): [{bytesBE[0]:X2} {bytesBE[1]:X2} {bytesBE[2]:X2} {bytesBE[3]:X2}]");
            
            // Address 0x013A4A: ANDI.W #$00FF,D0
            Console.WriteLine($"\n0x013A4A: ANDI.W #$00FF,D0");
            Console.WriteLine($"  Input: 0x{d0:X8}");
            
            // What ANDI.W should do:
            // - Read low 16 bits: 0x{(d0 & 0xFFFF):X4}
            // - AND with 0x00FF: 0x{(d0 & 0xFFFF) & 0x00FF:X4}
            // - Preserve high 16 bits: 0x{(d0 & 0xFFFF0000):X8}
            uint lowWord = d0 & 0xFFFF;
            uint andResult = lowWord & 0x00FF;
            d0 = (d0 & 0xFFFF0000) | andResult;
            Console.WriteLine($"  Output (correct): 0x{d0:X8}");
            Console.WriteLine($"  Low word: 0x{lowWord:X4} & 0x00FF = 0x{andResult:X4}");
            
            // But what if there's a bug?
            Console.WriteLine("\nPossible bugs:");
            Console.WriteLine("---------------");
            
            // Bug 1: ANDI.W reads wrong word (high word instead of low word)
            d0 = 0x07000000;
            uint highWord = (d0 >> 16) & 0xFFFF;
            andResult = highWord & 0x00FF;
            d0 = (d0 & 0xFFFF0000) | andResult;
            Console.WriteLine($"1. ANDI.W reads high word: 0x{highWord:X4} & 0x00FF = 0x{andResult:X4}");
            Console.WriteLine($"   Result: 0x{d0:X8}");
            
            // Bug 2: ANDI.W clears high bits
            d0 = 0x07000000;
            lowWord = d0 & 0xFFFF;
            andResult = lowWord & 0x00FF;
            d0 = andResult; // Clear high bits
            Console.WriteLine($"2. ANDI.W clears high bits: 0x{andResult:X8}");
            
            // Bug 3: Wrong immediate value
            d0 = 0x07000000;
            lowWord = d0 & 0xFFFF;
            andResult = lowWord & 0xFF00; // ANDI.W #$FF00 ?
            d0 = (d0 & 0xFFFF0000) | andResult;
            Console.WriteLine($"3. ANDI.W #$FF00: 0x{lowWord:X4} & 0xFF00 = 0x{andResult:X4}");
            Console.WriteLine($"   Result: 0x{d0:X8}");
            
            // Bug 4: Byte operation instead of word
            d0 = 0x07000000;
            uint lowByte = d0 & 0xFF;
            andResult = lowByte & 0xFF;
            d0 = (d0 & 0xFFFFFF00) | andResult;
            Console.WriteLine($"4. ANDI.B #$FF: low byte 0x{lowByte:X2} & 0xFF = 0x{andResult:X2}");
            Console.WriteLine($"   Result: 0x{d0:X8}");
            
            // What the game seems to need
            Console.WriteLine("\nWhat game needs:");
            Console.WriteLine("-----------------");
            Console.WriteLine("Index to non-zero table data: 0x00E0");
            Console.WriteLine("0x00E0 = 0x07 << 5");
            Console.WriteLine("So need low word = 0x0007 before ASL.W #5");
            Console.WriteLine("\nTo get low word = 0x0007 from 0x07000000:");
            Console.WriteLine("- Need to extract 0x07 from somewhere");
            Console.WriteLine("- 0x07 is in high byte (byte 3 in BE, byte 0 in LE)");
            Console.WriteLine("- ANDI.W #$00FF can't get it (operates on low word)");
            
            // Maybe it's a different instruction?
            Console.WriteLine("\nAlternative instructions:");
            Console.WriteLine("-------------------------");
            
            // ANDI.L #$000000FF,D0
            d0 = 0x07000000;
            d0 = d0 & 0x000000FF;
            Console.WriteLine($"ANDI.L #$000000FF,D0: 0x{d0:X8}");
            
            // ANDI.W #$FF00,D0 (to get high byte of low word, but low word is 0x0000)
            d0 = 0x07000000;
            lowWord = d0 & 0xFFFF;
            andResult = lowWord & 0xFF00;
            d0 = (d0 & 0xFFFF0000) | andResult;
            Console.WriteLine($"ANDI.W #$FF00,D0: 0x{d0:X8} (still 0x0000)");
            
            // What about ROR instead of ROL?
            d0 = 0x00070000;
            d0 = (d0 >> 8) | (d0 << 24); // ROR.L #8
            Console.WriteLine($"\nWhat if it's ROR.L #8 instead of ROL.L #8?");
            Console.WriteLine($"0x00070000 ROR.L #8 = 0x{((0x00070000 >> 8) | (0x00070000 << 24)):X8}");
            Console.WriteLine($"Then ANDI.W #$00FF would get 0x0007!");
        }
    }
}