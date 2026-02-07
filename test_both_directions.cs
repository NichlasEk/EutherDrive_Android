using System;

class TestBothDirections
{
    static uint ROL(uint value, int count)
    {
        for (int i = 0; i < count; i++)
        {
            bool msb = (value & 0x80000000) != 0;
            value = (value << 1);
            if (msb) value |= 0x01;
        }
        return value & 0xFFFFFFFF;
    }
    
    static uint ROR(uint value, int count)
    {
        for (int i = 0; i < count; i++)
        {
            bool lsb = (value & 0x01) != 0;
            value = value >> 1;
            if (lsb) value |= 0x80000000;
        }
        return value & 0xFFFFFFFF;
    }
    
    static uint ANDI_W(uint value, uint immediate)
    {
        // Word operation: AND low 16 bits, preserve high 16 bits
        return (value & 0xFFFF0000) | ((value & 0xFFFF) & (immediate & 0xFFFF));
    }
    
    static uint ASL_W(uint value, int count)
    {
        uint lowWord = value & 0xFFFF;
        uint highWord = value & 0xFFFF0000;
        
        for (int i = 0; i < count; i++)
        {
            bool msb = (lowWord & 0x8000) != 0;
            lowWord = (lowWord << 1) & 0xFFFF;
            // Carry goes to X flag, not back to value
        }
        
        return highWord | lowWord;
    }
    
    static void TestSequence(uint startD0, string rotation, int rotCount)
    {
        Console.WriteLine($"\nTest: Start D0=0x{startD0:X8}, {rotation}.L #{rotCount}");
        
        uint afterRot = rotation == "ROL" ? ROL(startD0, rotCount) : ROR(startD0, rotCount);
        uint afterAndi = ANDI_W(afterRot, 0x00FF);
        uint afterAsl = ASL_W(afterAndi, 5);
        
        Console.WriteLine($"  {rotation}.L #{rotCount}: 0x{startD0:X8} -> 0x{afterRot:X8}");
        Console.WriteLine($"  ANDI.W #$00FF: 0x{afterRot:X8} -> 0x{afterAndi:X8}");
        Console.WriteLine($"  ASL.W #5: 0x{afterAndi:X8} -> 0x{afterAsl:X8}");
        
        // Vad är ett "bra" resultat?
        // Palette index bör vara mellan 0x00000000 och 0x000001FF (512 entries)
        uint paletteIndex = afterAsl & 0xFFFF; // Low word
        if (paletteIndex <= 0x01FF)
        {
            Console.WriteLine($"  Palette index: 0x{paletteIndex:X4} (VALID: 0x0000-0x01FF)");
        }
        else
        {
            Console.WriteLine($"  Palette index: 0x{paletteIndex:X4} (INVALID: > 0x01FF)");
        }
    }
    
    static void Main()
    {
        Console.WriteLine("Testa alla möjliga rotationer för Madou buggen");
        Console.WriteLine("==============================================");
        
        // Möjliga startvärden från våra observationer
        uint[] startValues = { 0x00000007, 0x00000700, 0x00070000, 0x07000000 };
        
        // Testa båda riktningarna och olika counts
        string[] rotations = { "ROL", "ROR" };
        int[] counts = { 8, 16, 24 };
        
        foreach (uint start in startValues)
        {
            Console.WriteLine($"\n=== Start D0 = 0x{start:X8} ===");
            
            foreach (string rot in rotations)
            {
                foreach (int count in counts)
                {
                    TestSequence(start, rot, count);
                }
            }
        }
        
        // Vad ger "roterar åt fel håll"?
        Console.WriteLine("\n\nAnalys av 'roterar åt fel håll':");
        Console.WriteLine("Om palette index är för stort (> 0x01FF), kan det orsaka");
        Console.WriteLine("att fel färg hämtas från palette tabellen.");
        Console.WriteLine("Detta kan se ut som 'roterande' eller 'skiftande' färger.");
        
        // Kolla vad våra loggar visade för faktiskt resultat
        Console.WriteLine("\nFrån våra loggar:");
        Console.WriteLine("När D0 = 0x07000000 -> ROL.L #8 -> 0x00000007");
        Console.WriteLine("-> ANDI.W -> 0x00000007 -> ASL.W #5 -> 0x000000E0");
        Console.WriteLine("Palette index: 0x00E0 = 224 (VALID)");
        
        Console.WriteLine("\nNär D0 = 0x00070000 -> ROL.L #8 -> 0x07000000");
        Console.WriteLine("-> ANDI.W -> 0x07000000 -> ASL.W #5 -> 0x07000000");
        Console.WriteLine("Palette index: 0x0000 = 0 (VALID men kanske fel färg?)");
        
        // Kanske är 0x000000E0 fel index?
        Console.WriteLine("\nKanske är 0x000000E0 (224) fel palette index?");
        Console.WriteLine("Kanske ska det vara 0x00000700 eller något annat?");
        
        // Testa vad som händer om vi har fel ANDI.W implementation
        Console.WriteLine("\n--- Testa om ANDI.W är fel implementerad ---");
        Console.WriteLine("Vår ANDI.W: (value & 0xFFFF0000) | ((value & 0xFFFF) & immediate)");
        Console.WriteLine("Men om ANDI.W på ett register ska operera på HÖGA 16 bitar?");
        
        uint testVal = 0x07000000;
        uint ourANDI = (testVal & 0xFFFF0000) | ((testVal & 0xFFFF) & 0x00FF);
        uint altANDI = (testVal & 0x0000FFFF) | (((testVal >> 16) & 0x00FF) << 16);
        
        Console.WriteLine($"0x{testVal:X8} ANDI.W #$00FF:");
        Console.WriteLine($"  Vår implementation: 0x{ourANDI:X8}");
        Console.WriteLine($"  Alternativ (höga 16 bitar): 0x{altANDI:X8}");
        
        // Vilket ger bättre resultat?
        Console.WriteLine("\nMed alternativ ANDI.W:");
        Console.WriteLine($"0x07000000 -> ROL.L #8 -> 0x00000007");
        Console.WriteLine($"-> ALT ANDI.W -> 0x{((0x00000007 & 0x0000FFFF) | (((0x00000007 >> 16) & 0x00FF) << 16)):X8}");
    }
}