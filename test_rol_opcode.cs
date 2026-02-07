using System;

class TestROLOpcode
{
    static void Main()
    {
        // Testa opcode 0xE198 = ROL.L #?, D0
        ushort opcode = 0xE198; // 1110 0001 1001 1000
        
        // Standard 68000 opcode extrahering
        byte g_op1 = (byte)((opcode >> 9) & 0x07);  // bits 9-11
        byte g_op2 = (byte)((opcode >> 6) & 0x07);  // bits 6-8  
        byte g_op3 = (byte)((opcode >> 3) & 0x07);  // bits 3-5
        byte g_op4 = (byte)(opcode & 0x07);         // bits 0-2
        
        Console.WriteLine($"Opcode 0x{opcode:X4} = {Convert.ToString(opcode, 2).PadLeft(16, '0')}");
        Console.WriteLine($"g_op1 = {g_op1} (bits 9-11 = {Convert.ToString(g_op1, 2).PadLeft(3, '0')})");
        Console.WriteLine($"g_op2 = {g_op2} (bits 6-8 = {Convert.ToString(g_op2, 2).PadLeft(3, '0')})");
        Console.WriteLine($"g_op3 = {g_op3} (bits 3-5 = {Convert.ToString(g_op3, 2).PadLeft(3, '0')})");
        Console.WriteLine($"g_op4 = {g_op4} (bits 0-2 = {Convert.ToString(g_op4, 2).PadLeft(3, '0')})");
        
        // ROL.L format: 1110 0sss 1mm0 rrrr
        // sss = shift count (0=8, 1-7=1-7)
        // mm = size (00=byte, 01=word, 10=long)
        // rrrr = register
        
        // Extrahera enligt ROL format
        int sss = (opcode >> 9) & 0x07;      // bits 9-11
        int mm = (opcode >> 6) & 0x03;       // bits 6-7 (not 6-8!)
        int rrrr = opcode & 0x07;            // bits 0-2
        int bit8 = (opcode >> 8) & 0x01;     // bit 8
        
        Console.WriteLine($"\nEnligt ROL format:");
        Console.WriteLine($"sss (bits 9-11) = {sss} = shift count {(sss == 0 ? 8 : sss)}");
        Console.WriteLine($"mm (bits 6-7) = {mm} = size {(mm == 0 ? "byte" : mm == 1 ? "word" : "long")}");
        Console.WriteLine($"rrrr (bits 0-2) = {rrrr} = D{rrrr}");
        Console.WriteLine($"bit8 = {bit8}");
        
        // Testa 0xE098 = ROL.L #8, D0?
        opcode = 0xE098; // 1110 0000 1001 1000
        sss = (opcode >> 9) & 0x07;
        mm = (opcode >> 6) & 0x03;
        
        Console.WriteLine($"\nOpcode 0x{opcode:X4} = ROL.L #{(sss == 0 ? 8 : sss)}, D{opcode & 0x07}");
        Console.WriteLine($"sss = {sss}, mm = {mm}");
    }
}