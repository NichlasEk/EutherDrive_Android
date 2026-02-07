using System;

class TestROLSimple
{
    static uint ROL(uint value, int count, int size)
    {
        uint mask = size == 0 ? 0xFFu : size == 1 ? 0xFFFFu : 0xFFFFFFFFu;
        uint mostbit = size == 0 ? 0x80u : size == 1 ? 0x8000u : 0x80000000u;
        
        for (int i = 0; i < count; i++)
        {
            bool msb = (value & mostbit) != 0;
            value = (value << 1) & mask;
            if (msb) value |= 0x01;
        }
        return value & mask;
    }
    
    static uint ROR(uint value, int count, int size)
    {
        uint mask = size == 0 ? 0xFFu : size == 1 ? 0xFFFFu : 0xFFFFFFFFu;
        uint mostbit = size == 0 ? 0x80u : size == 1 ? 0x8000u : 0x80000000u;
        
        for (int i = 0; i < count; i++)
        {
            bool lsb = (value & 0x01) != 0;
            value = value >> 1;
            if (lsb) value |= mostbit;
        }
        return value & mask;
    }
    
    static void Main()
    {
        Console.WriteLine("Test ROL/ROR.L #8 på 0x00070000:");
        uint value = 0x00070000;
        
        uint rolResult = ROL(value, 8, 2); // size=2 (long)
        uint rorResult = ROR(value, 8, 2);
        
        Console.WriteLine($"0x{value:X8} ROL.L #8 = 0x{rolResult:X8}");
        Console.WriteLine($"0x{value:X8} ROR.L #8 = 0x{rorResult:X8}");
        
        Console.WriteLine("\nTest ROL.L #8 på 0x07000000:");
        value = 0x07000000;
        rolResult = ROL(value, 8, 2);
        rorResult = ROR(value, 8, 2);
        
        Console.WriteLine($"0x{value:X8} ROL.L #8 = 0x{rolResult:X8}");
        Console.WriteLine($"0x{value:X8} ROR.L #8 = 0x{rorResult:X8}");
        
        Console.WriteLine("\nTest opcode 0xE198 analys:");
        ushort opcode = 0xE198; // 1110 0001 1001 1000
        Console.WriteLine($"Opcode 0x{opcode:X4} = {Convert.ToString(opcode, 2).PadLeft(16, '0')}");
        
        // Extrahera enligt 68000 ROL/ROR format
        int sss = (opcode >> 9) & 0x07;      // bits 9-11: shift count
        int mm = (opcode >> 6) & 0x03;       // bits 6-7: size
        int bit3 = (opcode >> 3) & 0x01;     // bit 3: direction (0=ROR, 1=ROL)
        int register = opcode & 0x07;        // bits 0-2: register
        
        int shiftCount = sss == 0 ? 8 : sss;
        string sizeStr = mm == 0 ? "byte" : mm == 1 ? "word" : "long";
        string dirStr = bit3 == 0 ? "ROR" : "ROL";
        
        Console.WriteLine($"Decoded: {dirStr}.{sizeStr} #{(sss==0?8:sss)}, D{register}");
        Console.WriteLine($"  sss={sss} (shift count {shiftCount})");
        Console.WriteLine($"  mm={mm} ({sizeStr})");
        Console.WriteLine($"  bit3={bit3} ({dirStr})");
        Console.WriteLine($"  register=D{register}");
        
        // Testa vad som händer med bit 3
        Console.WriteLine($"\nBit 3 av 0xE198: {(opcode & 0x0008) != 0}");
        Console.WriteLine($"Bit 3 som heltal: {(opcode & 0x0008) >> 3}");
    }
}