using System;

class TestEndianIssue
{
    static void Main()
    {
        Console.WriteLine("Testa endianness för ANDI.W #$00FF, D0");
        Console.WriteLine("========================================");
        
        // Test different values
        uint[] testValues = {
            0x00000007,
            0x00000700,
            0x00070000,
            0x07000000,
            0x12345678,
            0xABCDEF12
        };
        
        foreach (uint value in testValues)
        {
            Console.WriteLine($"\nTest value: 0x{value:X8}");
            
            // Our current implementation (preserves high 16 bits)
            uint resultCurrent = (value & 0xFFFF0000) | ((value & 0xFFFF) & 0x00FF);
            Console.WriteLine($"  Current ANDI.W: 0x{resultCurrent:X8}");
            Console.WriteLine($"  Low 16 bits: 0x{(resultCurrent & 0xFFFF):X4}");
            
            // What if ANDI.W operates on HIGH 16 bits?
            uint resultHighBits = (value & 0x0000FFFF) | (((value >> 16) & 0xFFFF) & 0x00FF) << 16;
            Console.WriteLine($"  High bits ANDI.W: 0x{resultHighBits:X8}");
            Console.WriteLine($"  High 16 bits: 0x{(resultHighBits >> 16):X4}");
            
            // What if it's byte-order issue? Big-endian vs little-endian
            // In big-endian, byte 0 is most significant
            // So 0x07000000 in memory: 07 00 00 00
            // Low 16 bits (big-endian): 0x0700
            // High 16 bits (big-endian): 0x0000
            
            // Convert to big-endian bytes
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            
            Console.WriteLine($"  Big-endian bytes: {bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}");
            Console.WriteLine($"  Low 16 bits (big-endian): 0x{((bytes[2] << 8) | bytes[3]):X4}");
            Console.WriteLine($"  High 16 bits (big-endian): 0x{((bytes[0] << 8) | bytes[1]):X4}");
            
            // What if ANDI.W operates on big-endian low 16 bits?
            ushort low16BigEndian = (ushort)((bytes[2] << 8) | bytes[3]);
            ushort andResult = (ushort)(low16BigEndian & 0x00FF);
            
            // Convert back
            bytes[2] = (byte)(andResult >> 8);
            bytes[3] = (byte)(andResult & 0xFF);
            
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            uint resultBigEndianLow = BitConverter.ToUInt32(bytes, 0);
            Console.WriteLine($"  Big-endian low ANDI.W: 0x{resultBigEndianLow:X8}");
        }
        
        Console.WriteLine("\n\nTesta för Madou sekvensen:");
        Console.WriteLine("===========================");
        
        // Madou sequence: D0 = 0x07000000 -> ROL.L #8 -> ANDI.W #$00FF -> ASL.W #5
        uint d0 = 0x07000000;
        Console.WriteLine($"Start: D0 = 0x{d0:X8}");
        
        // ROL.L #8
        uint afterRol = (d0 << 8) | (d0 >> 24);
        Console.WriteLine($"ROL.L #8: 0x{afterRol:X8}");
        
        // Current ANDI.W
        uint afterAndiCurrent = (afterRol & 0xFFFF0000) | ((afterRol & 0xFFFF) & 0x00FF);
        Console.WriteLine($"Current ANDI.W: 0x{afterAndiCurrent:X8}");
        
        // ASL.W #5
        uint afterAsl = (afterAndiCurrent & 0xFFFF0000) | (((afterAndiCurrent & 0xFFFF) << 5) & 0xFFFF);
        Console.WriteLine($"ASL.W #5: 0x{afterAsl:X8}");
        Console.WriteLine($"Palette index: 0x{(afterAsl & 0xFFFF):X4}");
        
        // What if ANDI.W should zero high bits?
        uint afterAndiZeroHigh = afterRol & 0x0000FFFF & 0x00FF;
        Console.WriteLine($"\nANDI.W som nollställer high bits: 0x{afterAndiZeroHigh:X8}");
        uint afterAsl2 = (afterAndiZeroHigh << 5) & 0xFFFF;
        Console.WriteLine($"ASL.W #5: 0x{afterAsl2:X8}");
        Console.WriteLine($"Palette index: 0x{afterAsl2:X4}");
    }
}