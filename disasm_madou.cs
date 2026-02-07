using System;

class DisasmMadou
{
    // Simpel 68000 disassembler för att kolla opcodes
    static string Disassemble(ushort opcode, ushort nextWord1, ushort nextWord2)
    {
        int op = opcode >> 12;
        int mode = (opcode >> 3) & 0x07;
        int reg = opcode & 0x07;
        
        // Kolla ROL.L #8, D0
        if ((opcode & 0xF1F8) == 0xE198) // 1110 0001 1001 1000 mask
        {
            int count = (opcode >> 9) & 0x07;
            count = count == 0 ? 8 : count;
            return $"ROL.L #{(count == 8 ? "8" : count.ToString())}, D0";
        }
        
        // Kolla ANDI
        if ((opcode & 0xFF00) == 0x0200) // ANDI.B
        {
            return $"ANDI.B #${nextWord1 & 0x00FF:X2}, D{reg}";
        }
        else if ((opcode & 0xFF00) == 0x0240) // ANDI.W
        {
            return $"ANDI.W #${nextWord1:X4}, D{reg}";
        }
        else if ((opcode & 0xFF00) == 0x0280) // ANDI.L
        {
            return $"ANDI.L #${nextWord1:X4}{nextWord2:X4}, D{reg}";
        }
        
        // Kolla ASL
        if ((opcode & 0xF1F8) == 0xE1C8) // ASL.W #8, D0? Nej...
        {
            // ASL.W Dx, Dy format är komplext
            return $"ASL.W #?, D?";
        }
        
        return $"Unknown opcode: {opcode:X4}";
    }
    
    static void Main()
    {
        // Madou sekvens vid 0x013A4E-0x013A5C
        // Baserat på loggar: 0x013A4E: E198 (ROL.L #8, D0)
        // 0x013A50: ??? (PC efter ROL)
        // 0x013A58: ANDI.W #$00FF, D0
        // 0x013A5A: ASL.W #5, D0
        
        Console.WriteLine("Madou sekvens analys:");
        Console.WriteLine("0x013A4E: E198 = ROL.L #8, D0");
        Console.WriteLine("0x013A50: PC hoppar hit efter ROL");
        Console.WriteLine("0x013A58: ANDI instruktion");
        Console.WriteLine("0x013A5A: ASL instruktion");
        
        // Testa olika ANDI opcodes
        Console.WriteLine("\nMöjliga ANDI opcodes för D0:");
        Console.WriteLine("ANDI.B #$FF, D0 = 0200 00FF");
        Console.WriteLine("ANDI.W #$00FF, D0 = 0240 00FF");
        Console.WriteLine("ANDI.W #$FF00, D0 = 0240 FF00");
        Console.WriteLine("ANDI.L #$000000FF, D0 = 0280 0000 00FF");
        
        // Vad händer med 0x07000000?
        Console.WriteLine("\nTest 0x07000000 med olika ANDI:");
        uint d0 = 0x07000000;
        
        Console.WriteLine($"0x{d0:X8} ANDI.B #$FF = 0x{(d0 & 0x000000FF):X8}");
        Console.WriteLine($"0x{d0:X8} ANDI.W #$00FF = 0x{(d0 & 0x0000FFFF) & 0x00FF | (d0 & 0xFFFF0000):X8}");
        Console.WriteLine($"0x{d0:X8} ANDI.W #$FF00 = 0x{(d0 & 0x0000FFFF) & 0xFF00 | (d0 & 0xFFFF0000):X8}");
        Console.WriteLine($"0x{d0:X8} ANDI.L #$000000FF = 0x{(d0 & 0x000000FF):X8}");
        Console.WriteLine($"0x{d0:X8} ANDI.L #$00FF0000 = 0x{(d0 & 0x00FF0000):X8}");
        Console.WriteLine($"0x{d0:X8} ANDI.L #$FF000000 = 0x{(d0 & 0xFF000000):X8}");
        
        // Vad behöver spelet?
        Console.WriteLine("\nSpelet behöver efter ANDI: 0x00000007");
        Console.WriteLine("Efter ASL.W #5: 0x000000E0 (0x07 << 5 = 0xE0)");
        
        // För att få 0x00000007 från 0x07000000:
        Console.WriteLine("\nFör att få 0x00000007 från 0x07000000:");
        Console.WriteLine("- ANDI.L #$00000007 = 0x00000007 ✓");
        Console.WriteLine("- ANDI.W #$0007 (men det ger 0x07000007, inte 0x00000007)");
        Console.WriteLine("- ANDI.B #$07 (ger 0x07000000, inte 0x00000007)");
        
        // Kanske är det ANDI.L #$000000FF?
        Console.WriteLine("\nANDI.L #$000000FF på 0x07000000 = 0x00000000 ✗");
        
        // Kanske är det ANDI.L #$FF000000?
        Console.WriteLine("ANDI.L #$FF000000 på 0x07000000 = 0x07000000 ✗");
        
        // Kanske är det ANDI.L #$00000700?
        Console.WriteLine("ANDI.L #$00000700 på 0x07000000 = 0x00000000 ✗");
        
        // Vänta! Kanske är D0 inte 0x07000000 efter ROL.L #8?
        // Kanske är ROL.L #8 implementerad fel?
        Console.WriteLine("\n--- Test ROL.L #8 implementation ---");
        uint testVal = 0x00070000;
        for (int i = 0; i < 8; i++)
        {
            bool msb = (testVal & 0x80000000) != 0;
            testVal = (testVal << 1);
            if (msb) testVal |= 0x01;
        }
        Console.WriteLine($"0x00070000 ROL.L #8 = 0x{testVal:X8} (förväntat: 0x07000000)");
        
        // Testa om det är ROR.L #8
        testVal = 0x00070000;
        for (int i = 0; i < 8; i++)
        {
            bool lsb = (testVal & 0x01) != 0;
            testVal = (testVal >> 1);
            if (lsb) testVal |= 0x80000000;
        }
        Console.WriteLine($"0x00070000 ROR.L #8 = 0x{testVal:X8} (0x00000700)");
        
        // Testa ROL.L #24 (samma som ROR.L #8)
        testVal = 0x00070000;
        for (int i = 0; i < 24; i++)
        {
            bool msb = (testVal & 0x80000000) != 0;
            testVal = (testVal << 1);
            if (msb) testVal |= 0x01;
        }
        Console.WriteLine($"0x00070000 ROL.L #24 = 0x{testVal:X8} (samma som ROR.L #8)");
    }
}