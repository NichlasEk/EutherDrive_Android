using System;

class TestRORInstead
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
    
    static void Main()
    {
        Console.WriteLine("Test om ROR.L #8 ger bättre resultat:");
        
        // Testa med start D0 = 0x07000000 (vad vi tror är rätt)
        uint d0 = 0x07000000;
        
        Console.WriteLine($"Start: D0 = 0x{d0:X8}");
        Console.WriteLine($"ROL.L #8: 0x{ROL(d0, 8):X8}");
        Console.WriteLine($"ROR.L #8: 0x{ROR(d0, 8):X8}");
        Console.WriteLine($"ROR.L #24: 0x{ROR(d0, 24):X8} (samma som ROL.L #8)");
        
        // Vad händer med ANDI.W efteråt?
        Console.WriteLine("\nEfter rotation, ANDI.W #$00FF:");
        uint afterROL = ROL(d0, 8); // 0x00000007
        uint afterROR = ROR(d0, 8); // 0x00070000
        uint afterROR24 = ROR(d0, 24); // 0x00000007
        
        Console.WriteLine($"ROL.L #8 -> 0x{afterROL:X8} -> ANDI.W -> 0x{(afterROL & 0x0000FFFF) & 0x00FF | (afterROL & 0xFFFF0000):X8}");
        Console.WriteLine($"ROR.L #8 -> 0x{afterROR:X8} -> ANDI.W -> 0x{(afterROR & 0x0000FFFF) & 0x00FF | (afterROR & 0xFFFF0000):X8}");
        Console.WriteLine($"ROR.L #24 -> 0x{afterROR24:X8} -> ANDI.W -> 0x{(afterROR24 & 0x0000FFFF) & 0x00FF | (afterROR24 & 0xFFFF0000):X8}");
        
        // Testa vad som händer med olika startvärden
        Console.WriteLine("\n--- Testa hela cykeln med ROR.L #8 ---");
        uint[] cycleValues = { 0x00000007, 0x00000700, 0x00070000, 0x07000000 };
        
        foreach (uint val in cycleValues)
        {
            uint rorResult = ROR(val, 8);
            uint andiResult = (rorResult & 0x0000FFFF) & 0x00FF | (rorResult & 0xFFFF0000);
            uint aslResult = (andiResult & 0x0000FFFF) << 5 | (andiResult & 0xFFFF0000);
            
            Console.WriteLine($"0x{val:X8} -> ROR.L #8 -> 0x{rorResult:X8} -> ANDI.W -> 0x{andiResult:X8} -> ASL.W #5 -> 0x{aslResult:X8}");
        }
        
        // Kanske behöver vi ROR.L #24?
        Console.WriteLine("\n--- Testa med ROR.L #24 ---");
        foreach (uint val in cycleValues)
        {
            uint rorResult = ROR(val, 24); // Samma som ROL.L #8
            uint andiResult = (rorResult & 0x0000FFFF) & 0x00FF | (rorResult & 0xFFFF0000);
            uint aslResult = (andiResult & 0x0000FFFF) << 5 | (andiResult & 0xFFFF0000);
            
            Console.WriteLine($"0x{val:X8} -> ROR.L #24 -> 0x{rorResult:X8} -> ANDI.W -> 0x{andiResult:X8} -> ASL.W #5 -> 0x{aslResult:X8}");
        }
        
        // Vad säger grafiken "roterar åt fel håll"?
        Console.WriteLine("\n--- Analys av 'roterar åt fel håll' ---");
        Console.WriteLine("Om grafiken roterar åt fel håll, kanske:");
        Console.WriteLine("1. ROL istället för ROR (eller tvärtom)");
        Console.WriteLine("2. Fel rotation count (8 vs 24 vs 16)");
        Console.WriteLine("3. Fel startvärde i D0");
        Console.WriteLine("4. ANDI.W läser/skriver fel del av register");
        
        // Testa om vår w_dr fix kan vara fel
        Console.WriteLine("\n--- Kolla vår w_dr logik ---");
        ushort opcode = 0xE198; // 1110 0001 1001 1000
        int bit3 = (opcode >> 3) & 0x1;
        Console.WriteLine($"Opcode 0x{opcode:X4} = {Convert.ToString(opcode, 2).PadLeft(16, '0')}");
        Console.WriteLine($"Bit 3 (0=ROR, 1=ROL) = {bit3}");
        Console.WriteLine($"Vår kod: w_dr = (g_opcode & 0x0008) != 0 ? 1 : 0");
        Console.WriteLine($"g_opcode & 0x0008 = 0x{opcode & 0x0008:X4} = {((opcode & 0x0008) != 0)}");
        Console.WriteLine($"Så w_dr = {((opcode & 0x0008) != 0 ? 1 : 0)} ({(bit3 == 1 ? "ROL" : "ROR")})");
    }
}