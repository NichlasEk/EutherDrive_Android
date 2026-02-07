using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Explicit)]
struct md_u32
{
    [FieldOffset(0)] public uint   l;     // 32-bit
    [FieldOffset(0)] public ushort w;     // low 16
    [FieldOffset(2)] public ushort wup;   // high 16

    [FieldOffset(0)] public byte b0;
    [FieldOffset(1)] public byte b1;
    [FieldOffset(2)] public byte b2;
    [FieldOffset(3)] public byte b3;
}

class TestBigEndianRegisters
{
    static void Main()
    {
        Console.WriteLine("Testa md_u32 struktur (little-endian vs big-endian)");
        Console.WriteLine("===================================================");
        
        md_u32 reg = new md_u32();
        reg.l = 0x12345678;
        
        Console.WriteLine($"32-bit value: 0x{reg.l:X8}");
        Console.WriteLine($"Low 16 bits (.w): 0x{reg.w:X4}");
        Console.WriteLine($"High 16 bits (.wup): 0x{reg.wup:X4}");
        Console.WriteLine($"Bytes: b0=0x{reg.b0:X2}, b1=0x{reg.b1:X2}, b2=0x{reg.b2:X2}, b3=0x{reg.b3:X2}");
        
        // Check endianness
        byte[] bytes = BitConverter.GetBytes(reg.l);
        Console.WriteLine($"\nBitConverter bytes (system endian): {bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}");
        Console.WriteLine($"System is little-endian: {BitConverter.IsLittleEndian}");
        
        // What should 68000 (big-endian) see?
        // 0x12345678 in big-endian: 12 34 56 78
        // Low 16 bits (big-endian): 0x5678 (bytes 2-3)
        // High 16 bits (big-endian): 0x1234 (bytes 0-1)
        
        Console.WriteLine("\n68000 big-endian perspective:");
        Console.WriteLine("  Value: 0x12345678");
        Console.WriteLine("  Bytes in memory: 12 34 56 78");
        Console.WriteLine("  Low 16 bits (bytes 2-3): 0x5678");
        Console.WriteLine("  High 16 bits (bytes 0-1): 0x1234");
        
        // Test Madou values
        Console.WriteLine("\n--- Madou test values ---");
        
        uint[] testValues = { 0x00000007, 0x00000700, 0x00070000, 0x07000000 };
        
        foreach (uint value in testValues)
        {
            reg.l = value;
            Console.WriteLine($"\nValue: 0x{value:X8}");
            Console.WriteLine($"  .w (little-endian low): 0x{reg.w:X4}");
            Console.WriteLine($"  .wup (little-endian high): 0x{reg.wup:X4}");
            
            // What would big-endian low/high be?
            byte[] beBytes = new byte[4];
            beBytes[0] = (byte)(value >> 24);
            beBytes[1] = (byte)(value >> 16);
            beBytes[2] = (byte)(value >> 8);
            beBytes[3] = (byte)value;
            
            ushort beLow = (ushort)((beBytes[2] << 8) | beBytes[3]);
            ushort beHigh = (ushort)((beBytes[0] << 8) | beBytes[1]);
            
            Console.WriteLine($"  Big-endian low (bytes 2-3): 0x{beLow:X4}");
            Console.WriteLine($"  Big-endian high (bytes 0-1): 0x{beHigh:X4}");
            
            // ANDI.W #$00FF on big-endian low
            ushort andResult = (ushort)(beLow & 0x00FF);
            Console.WriteLine($"  ANDI.W #$00FF on big-endian low: 0x{andResult:X4}");
            
            // What if ANDI.W operates on little-endian .w?
            ushort leAndResult = (ushort)(reg.w & 0x00FF);
            Console.WriteLine($"  ANDI.W #$00FF on little-endian .w: 0x{leAndResult:X4}");
        }
        
        // Test the Madou sequence
        Console.WriteLine("\n--- Madou sequence test ---");
        Console.WriteLine("D0 = 0x07000000");
        Console.WriteLine("ROL.L #8 -> 0x00000007");
        
        reg.l = 0x00000007;
        Console.WriteLine($"After ROL: 0x{reg.l:X8}");
        Console.WriteLine($"  .w (little-endian): 0x{reg.w:X4}");
        Console.WriteLine($"  Big-endian low: 0x{((reg.b2 << 8) | reg.b3):X4}");
        
        // ANDI.W #$00FF
        ushort andResult1 = (ushort)(reg.w & 0x00FF);
        Console.WriteLine($"  ANDI.W on .w: 0x{andResult1:X4}");
        
        // What if we need to AND big-endian low?
        ushort beLow2 = (ushort)((reg.b2 << 8) | reg.b3);
        ushort andResult2 = (ushort)(beLow2 & 0x00FF);
        Console.WriteLine($"  ANDI.W on big-endian low: 0x{andResult2:X4}");
        
        // ASL.W #5
        uint aslResult1 = (uint)(andResult1 << 5) & 0xFFFF;
        uint aslResult2 = (uint)(andResult2 << 5) & 0xFFFF;
        Console.WriteLine($"  ASL.W #5 on .w result: 0x{aslResult1:X4}");
        Console.WriteLine($"  ASL.W #5 on big-endian result: 0x{aslResult2:X4}");
        
        Console.WriteLine("\n--- Conclusion ---");
        Console.WriteLine("If md_u32 is little-endian but 68000 is big-endian,");
        Console.WriteLine("then ANDI.W #$00FF, D0 operates on WRONG 16 bits!");
        Console.WriteLine("It should operate on big-endian low 16 bits (bytes 2-3),");
        Console.WriteLine("not little-endian low 16 bits (bytes 0-1).");
    }
}